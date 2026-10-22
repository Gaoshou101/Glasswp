using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WallpaperBridge;

public sealed record QuotaRefreshCheckpoint(string SourceKey, QuotaRefreshState State);
public sealed record QuotaRefreshReport(bool Enabled, int IntervalMinutes, DateTimeOffset? NextRefreshAt, QuotaRefreshState State);

// One bridge owns the schedule and merges manual requests from settings and wallpapers.
public sealed class UpstreamQuotaRefresher
{
    private readonly PrivateStore store;
    private readonly HttpClient client;
    private readonly Func<DateTimeOffset> clock;
    private readonly object stateGate = new();
    private readonly SemaphoreSlim runGate = new(1, 1);
    private string sourceKey;
    private QuotaRefreshState state;
    private bool queued;

    public UpstreamQuotaRefresher(PrivateStore store, HttpClient client, Func<DateTimeOffset>? clock = null)
    {
        this.store = store; this.client = client; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        sourceKey = SourceKey(store.Config);
        var saved = store.ReadCache<QuotaRefreshCheckpoint>("quota-refresh", checkpoint => checkpoint.State is not null && checkpoint.State.Accounts is not null);
        state = saved?.SourceKey == sourceKey ? saved.State : store.CacheFaults.Contains("quota-refresh:read") ? new() { LastAttemptAt = this.clock(), LastFinishedAt = this.clock(), Status = "interrupted", Message = "刷新记录不可读取，按设定间隔重试" } : new();
        if (state.Status is "running" or "queued") state = state with { Status = "interrupted", Message = "上次刷新已中断，将按设定间隔继续" };
    }

    private static string SourceKey(BridgeConfig cfg) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { url = cfg.CpampUrl.TrimEnd('/'), key = cfg.ManagementKey,
            accounts = cfg.Accounts.Select(a => new { a.Id, a.Provider, a.AuthFile }).OrderBy(a => a.Id) }))));
    private void SyncSource(BridgeConfig cfg)
    {
        var key = SourceKey(cfg);
        if (key == sourceKey) return;
        sourceKey = key; state = new(); queued = false;
        Persist();
    }
    private bool Persist() => store.WriteCache("quota-refresh", new QuotaRefreshCheckpoint(sourceKey, state));
    public QuotaRefreshReport Report()
    {
        lock (stateGate)
        {
            var cfg = store.Config; SyncSource(cfg);
            var next = state.Status is "running" or "queued" ? null : QuotaRefreshSchedule.Next(cfg, state, clock());
            return new(cfg.QuotaRefreshEnabled, cfg.QuotaRefreshMinutes, next, state);
        }
    }
    public bool Queue()
    {
        lock (stateGate)
        {
            var cfg = store.Config; SyncSource(cfg);
            if (string.IsNullOrEmpty(cfg.ManagementKey) || cfg.Accounts.Length == 0) throw new InvalidOperationException("请先保存连接并选择关注账号");
            if (state.Status is "running" or "queued") return false;
            queued = true; state = state with { Status = "queued", Message = "已排队，正在准备向上游刷新", Accounts = [] };
            return true;
        }
    }
    private bool Current(string key, bool manual) => SourceKey(store.Config) == key && (manual || store.Config.QuotaRefreshEnabled);
    private HttpRequestMessage Request(BridgeConfig cfg, HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, cfg.CpampUrl.TrimEnd('/') + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ManagementKey);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    public async Task<bool> Tick(CancellationToken token)
    {
        if (!await runGate.WaitAsync(0, token)) return false;
        try
        {
            BridgeConfig cfg; string key; bool manual;
            lock (stateGate)
            {
                cfg = store.Config; SyncSource(cfg); manual = queued;
                if (!manual && !QuotaRefreshSchedule.Due(cfg, state, clock())) return false;
                queued = false; key = sourceKey;
                state = state with { LastAttemptAt = clock(), Status = "running", Message = "正在向上游获取用量", Accounts = [] };
                // Save before sending so a crash or restart cannot repeatedly query upstream.
                if (!Persist()) {
                    state = state with { LastFinishedAt = clock(), Status = "failed", Message = "刷新记录无法保存，等待下次尝试" };
                    return false;
                }
            }
            var results = new List<RefreshAccountResult>();
            try
            {
                using var listRequest = Request(cfg, HttpMethod.Get, "/v0/management/auth-files");
                using var listResponse = await client.SendAsync(listRequest, token);
                listResponse.EnsureSuccessStatusCode();
                using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(token));
                var files = QuotaAdapter.Unwrap(listDoc.RootElement).GetProperty("files").EnumerateArray().ToArray();
                foreach (var selection in cfg.Accounts)
                {
                    if (!Current(key, manual)) break;
                    token.ThrowIfCancellationRequested();
                    RefreshAccountResult result;
                    if (selection.Provider != "codex") result = new(selection.Alias, "unsupported", "该平台暂不支持自动刷新；继续展示已有快照");
                    else
                    {
                        try
                        {
                            var file = files.FirstOrDefault(f => QuotaAdapter.Text(f, "name") == selection.AuthFile && QuotaAdapter.Text(f, "provider", QuotaAdapter.Text(f, "type")).Equals(selection.Provider, StringComparison.OrdinalIgnoreCase));
                            if (file.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
                            var account = CachedHeaderAdapter.Identity(file, selection);
                            if (string.IsNullOrEmpty(account.AuthIndex) || string.IsNullOrEmpty(account.AccountId)) throw new InvalidOperationException();
                            using var request = Request(cfg, HttpMethod.Post, "/v0/management/api-call", UpstreamQuotaAdapter.Request(account));
                            using var response = await client.SendAsync(request, token);
                            response.EnsureSuccessStatusCode();
                            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                            var wrapper = QuotaAdapter.Unwrap(doc.RootElement);
                            var statusCode = QuotaAdapter.Number(wrapper, "status_code") ?? QuotaAdapter.Number(wrapper, "statusCode");
                            if (statusCode is not (>= 200 and < 300)) throw new HttpRequestException(null, null, statusCode is >= 100 and <= 599 ? (HttpStatusCode)(int)statusCode : null);
                            var body = wrapper.GetProperty("body");
                            using var usage = JsonDocument.Parse(body.ValueKind == JsonValueKind.String ? body.GetString()! : body.GetRawText());
                            var windows = UpstreamQuotaAdapter.Windows(usage.RootElement, clock());
                            if (!Current(key, manual)) break;
                            using var writeRequest = Request(cfg, HttpMethod.Post, "/v0/management/quota-snapshots", UpstreamQuotaAdapter.SnapshotWrite(account, windows));
                            using var writeResponse = await client.SendAsync(writeRequest, token);
                            writeResponse.EnsureSuccessStatusCode();
                            using var written = JsonDocument.Parse(await writeResponse.Content.ReadAsStringAsync(token));
                            var items = QuotaAdapter.Unwrap(written.RootElement).GetProperty("items");
                            if (items.ValueKind != JsonValueKind.Array || !items.EnumerateArray().Any(i => QuotaAdapter.Text(i, "row_key") == account.Id && QuotaAdapter.Number(i, "inserted_count") == windows.Length)) throw new JsonException();
                            result = new(selection.Alias, "ok", $"已更新 {windows.Length} 个额度窗口");
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (HttpRequestException e) { result = new(selection.Alias, "failed", Failure(e)); }
                        catch (TaskCanceledException) { result = new(selection.Alias, "failed", "请求超时，保留上次快照"); }
                        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
                        { result = new(selection.Alias, "failed", "账号身份或额度响应不可读取，保留上次快照"); }
                    }
                    results.Add(result);
                    lock (stateGate) if (sourceKey == key) state = state with { Accounts = results.ToArray() };
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                var message = e is HttpRequestException http ? Failure(http) : "无法读取 CPAMP 账号列表，保留上次快照";
                results.AddRange(cfg.Accounts.Skip(results.Count).Select(a => new RefreshAccountResult(a.Alias, "failed", message)));
            }
            lock (stateGate)
            {
                SyncSource(store.Config);
                if (sourceKey != key) return false;
                var succeeded = results.Count(r => r.Status == "ok");
                var cancelled = results.Count != cfg.Accounts.Length;
                state = state with { LastFinishedAt = clock(), LastSuccessAt = succeeded > 0 ? clock() : state.LastSuccessAt,
                    Status = cancelled ? "cancelled" : succeeded == cfg.Accounts.Length ? "ok" : succeeded > 0 ? "partial" : "failed",
                    Message = cancelled ? "刷新已停止，保留已有结果" : $"{succeeded}/{cfg.Accounts.Length} 个账号已更新" + (succeeded < cfg.Accounts.Length ? "；其余保留上次快照" : ""), Accounts = results.ToArray() };
                Persist();
            }
            return true;
        }
        finally { runGate.Release(); }
    }
    private static string Failure(HttpRequestException e) => e.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "鉴权失败，请检查 CPAMP 连接与账号授权",
        HttpStatusCode.TooManyRequests => "上游请求受限，等待下次刷新",
        _ => "远端请求失败，保留上次快照"
    };
}
