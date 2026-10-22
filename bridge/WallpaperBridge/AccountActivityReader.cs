using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WallpaperBridge;

/// <summary>Only the configured credential's scoped summary and a bounded page of safe rows.</summary>
public sealed class AccountActivityReader(PrivateStore store, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (AccountActivity Data, DateTimeOffset Next)> cache = [];
    private long revision = -1;
    public async Task<AccountActivity?> Read(string id, CancellationToken token, ActivityQuery? query = null)
    {
        await gate.WaitAsync(token);
        try
        {
            if (revision != store.Revision) { cache.Clear(); revision = store.Revision; }
            var cfg = store.Config;
            var account = cfg.Accounts.FirstOrDefault(a => a.Id == id);
            if (account is null) return null;
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            query ??= new();
            var bounds = query.Bounds(now);
            var key = JsonSerializer.Serialize(new { id, query, from = bounds.From.ToUnixTimeMilliseconds() });
            cache.TryGetValue(key, out var previous);
            if (previous.Data is not null && now < previous.Next) return previous.Data;
            var empty = new AccountActivity(id, "empty", null, bounds.From, bounds.To, null, null, null, null, null, [], false, "暂无请求记录");
            if (string.IsNullOrEmpty(cfg.ManagementKey)) return empty with { Status = "unconfigured", Message = "请在本机设置连接 CPAMP" };
            AccountActivity data;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, cfg.CpampUrl.TrimEnd('/') + "/v0/management/monitoring/analytics");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ManagementKey);
                request.Content = JsonContent.Create(BuildRequest(account, empty.From, empty.To, query, now));
                using var response = await client.SendAsync(request, token);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                    data = empty with { Status = "unsupported", Message = "当前 CPAMP 版本未提供请求活动" };
                else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    data = empty with { Status = "unauthorized", Message = "请求活动鉴权失败，请检查本机设置" };
                else
                {
                    response.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    data = Parse(doc.RootElement, account, empty.From, empty.To) with { FetchedAt = now, Message = query.Label + " · CPAMP 请求监控" };
                    if (query.BeforeMs is not null) data = data with { LastActivity = cache.Values.Select(p => p.Data).FirstOrDefault(p => p.AccountId == id && p.From.ToUnixTimeMilliseconds() == empty.From.ToUnixTimeMilliseconds() && p.To.ToUnixTimeMilliseconds() == empty.To.ToUnixTimeMilliseconds())?.LastActivity };
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
            {
                data = previous.Data?.FetchedAt is not null ? previous.Data with { Status = "stale", Message = "连接暂不可用，显示上次请求活动" }
                    : empty with { Status = "offline", Message = "请求活动暂不可用，可稍后重试" };
            }
            // Discard an in-flight result if credentials or selection changed.
            if (cfg != store.Config) return null;
            foreach (var expired in cache.Where(p => p.Value.Next < now.AddMinutes(-5)).Select(p => p.Key).ToArray()) cache.Remove(expired);
            if (cache.Count >= 128 && !cache.ContainsKey(key)) cache.Remove(cache.MinBy(p => p.Value.Next).Key);
            cache[key] = (data, now.AddSeconds(data.Status is "ok" or "empty" ? 60 : 15));
            return data;
        }
        finally { gate.Release(); }
    }
    public static object BuildRequest(AccountSelection account, DateTimeOffset from, DateTimeOffset to, ActivityQuery? query = null, DateTimeOffset? observedNow = null) => new {
        from_ms = from.ToUnixTimeMilliseconds(), to_ms = to.ToUnixTimeMilliseconds(), now_ms = (observedNow ?? to).ToUnixTimeMilliseconds(),
        time_zone = query?.TimeZone ?? "",
        filters = new { providers = new[] { account.Provider }, auth_files = new[] { account.AuthFile }, include_failed = true },
        include = new { summary = true, summary_profile = "compact", events_page = new { limit = 20, before_ms = query?.BeforeMs, before_id = query?.BeforeId } }
    };
    private static long? Count(JsonElement node, string key) => QuotaAdapter.Number(node, key) is >= 0 and <= 9007199254740991 and var n ? (long)n : null;
    private static double? Amount(JsonElement node, string key) => QuotaAdapter.Number(node, key) is >= 0 and var n ? n : null;
    public static AccountActivity Parse(JsonElement response, AccountSelection account, DateTimeOffset from, DateTimeOffset to)
    {
        var root = QuotaAdapter.Unwrap(response);
        if (!root.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Object ||
            !events.TryGetProperty("items", out var rows) || rows.ValueKind != JsonValueKind.Array) throw new JsonException("Missing account analytics");
        var result = new List<RequestActivity>();
        foreach (var row in rows.EnumerateArray().Take(20))
        {
            // Do not expose a global or a different credential's response by accident.
            if (row.ValueKind != JsonValueKind.Object || QuotaAdapter.Text(row, "auth_file_snapshot") != account.AuthFile ||
                !QuotaAdapter.Text(row, "auth_provider_snapshot").Equals(account.Provider, StringComparison.OrdinalIgnoreCase)) throw new JsonException("Mismatched credential analytics");
            var time = QuotaAdapter.Timestamp(row, "timestamp_ms");
            if (time is null || time < from || time >= to) continue;
            var model = QuotaAdapter.Text(row, "analytics_model", QuotaAdapter.Text(row, "model"));
            model = new string(model.Where(c => !char.IsControl(c)).Take(80).ToArray());
            bool? success = row.TryGetProperty("failed", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.ValueKind == JsonValueKind.False : null;
            var status = Count(row, "fail_status_code");
            result.Add(new(time.Value, model, success, status is >= 100 and <= 599 ? (int)status : null,
                Count(row, "total_tokens"), Count(row, "input_tokens"), Count(row, "output_tokens"), Count(row, "cache_read_tokens"), Amount(row, "latency_ms"), Amount(row, "ttft_ms")));
        }
        var recent = result.OrderByDescending(r => r.Time).ToArray();
        var requests = Count(summary, "total_calls");
        if (requests is null || requests < recent.Length) throw new JsonException("Invalid request count");
        var rate = QuotaAdapter.Number(summary, "success_rate");
        return new(account.Id, requests == 0 ? "empty" : "ok", to, from, to, requests,
            requests > 0 && rate is >= 0 and <= 1 ? rate * 100 : null, Count(summary, "total_tokens"), Amount(summary, "total_cost"), recent.FirstOrDefault()?.Time,
            recent, events.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True, "CPAMP 请求监控",
            Count(events, "next_before_ms") is > 0 and var nextMs ? nextMs : null,
            Count(events, "next_before_id") is > 0 and var nextId ? nextId : null, Count(events, "total_count"));
    }
    public void Dispose() { client.Dispose(); gate.Dispose(); }
}
