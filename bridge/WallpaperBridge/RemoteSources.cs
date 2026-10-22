using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WallpaperBridge;

public sealed class RemoteSources(PrivateStore store, DataHub hub, HttpMessageHandler? handler = null) : BackgroundService
{
    private readonly HttpClient client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    private DateTimeOffset nextQuota, nextWeather;
    private long revision = store.Revision;
    private long refreshGeneration;
    private UpstreamQuotaRefresher? refresher;
    private LocalQuotaClassifier quotaClassifier = new();
    private UpstreamQuotaRefresher Refresher => LazyInitializer.EnsureInitialized(ref refresher, () => new(store, client));
    public QuotaRefreshReport RefreshStatus() => Refresher.Report();
    public bool QueueQuotaRefresh() => Refresher.Queue();
    public void RefreshCachedSources() => Interlocked.Increment(ref refreshGeneration);

    public HttpRequestMessage CpampRequest(HttpMethod method, string path, BridgeConfig? config = null)
    {
        var cfg = config ?? store.Config;
        var req = new HttpRequestMessage(method, cfg.CpampUrl.TrimEnd('/') + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ManagementKey);
        return req;
    }
    public async Task<AccountSelection[]> DiscoverAccounts(CancellationToken token)
    {
        if (string.IsNullOrEmpty(store.Config.ManagementKey)) throw new InvalidOperationException("请先填写 CPAMP 管理凭据");
        using var req = CpampRequest(HttpMethod.Get, "/v0/management/auth-files");
        using var response = await client.SendAsync(req, token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = QuotaAdapter.Unwrap(doc.RootElement);
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) throw new JsonException("Missing files");
        var result = new List<AccountSelection>();
        foreach (var file in files.EnumerateArray())
        {
            var provider = QuotaAdapter.Text(file, "provider", QuotaAdapter.Text(file, "type")).ToLowerInvariant();
            if (!new[] { "codex", "claude", "antigravity", "kimi", "xai", "devin", "meta" }.Contains(provider)) continue;
            var name = QuotaAdapter.Text(file, "name");
            var index = CachedHeaderAdapter.StringOrNumber(file, "auth_index");
            var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provider + ":" + name + ":" + index)))[..16].ToLowerInvariant();
            var selected = store.Config.Accounts.FirstOrDefault(a => a.Id == id);
            result.Add(CachedHeaderAdapter.Identity(file, new(id, selected?.Alias ?? provider + " " + (result.Count + 1), provider, name, index, "")));
        }
        return result.ToArray();
    }
    public async Task<City[]> SearchCities(string query, CancellationToken token)
    {
        if (query.Trim().Length is < 2 or > 80) return [];
        using var response = await client.GetAsync("https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(query.Trim()) + "&count=8&language=zh&format=json", token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];
        return results.EnumerateArray().Select(r => new City(QuotaAdapter.Number(r, "id")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", QuotaAdapter.Text(r, "name") + " · " + QuotaAdapter.Text(r, "admin1", QuotaAdapter.Text(r, "country")), QuotaAdapter.Number(r, "latitude") ?? 0, QuotaAdapter.Number(r, "longitude") ?? 0, QuotaAdapter.Text(r, "timezone", "Asia/Shanghai"))).ToArray();
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        Task quotaWork = Task.CompletedTask, weatherWork = Task.CompletedTask;
        Task<bool> upstreamWork = Task.FromResult(false);
        long appliedGeneration = -1;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (revision != store.Revision)
                {
                    revision = store.Revision;
                    quotaClassifier = new();
                    // Never show a removed account or another city's last data after configuration changes.
                    hub.Quota(Empty.Quota);
                    hub.Weather(Empty.Weather(store.Config.City));
                    RefreshCachedSources();
                }
                var generation = Interlocked.Read(ref refreshGeneration);
                if (generation != appliedGeneration)
                {
                    appliedGeneration = generation;
                    nextQuota = nextWeather = DateTimeOffset.MinValue;
                }
                // Each source has one in-flight task. Slow weather never holds quota reads.
                if (upstreamWork.IsCompleted)
                {
                    if (await upstreamWork) nextQuota = DateTimeOffset.MinValue;
                    upstreamWork = Refresher.Tick(stoppingToken);
                }
                if (weatherWork.IsCompleted && DateTimeOffset.UtcNow >= nextWeather)
                { await weatherWork; nextWeather = DateTimeOffset.UtcNow.AddMinutes(15); weatherWork = UpdateWeather(stoppingToken); }
                if (quotaWork.IsCompleted && DateTimeOffset.UtcNow >= nextQuota)
                { await quotaWork; nextQuota = DateTimeOffset.UtcNow.AddSeconds(60); quotaWork = UpdateQuota(stoppingToken); }
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            try { await Task.WhenAll(quotaWork, weatherWork, upstreamWork); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            client.Dispose();
        }
    }
    private static bool SourceFailure(Exception e) => e is HttpRequestException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException;
    internal Task ReadQuotaOnce(CancellationToken token) => UpdateQuota(token);
    private async Task UpdateQuota(CancellationToken token)
    {
        var cfg = store.Config;
        if (string.IsNullOrEmpty(cfg.ManagementKey)) { hub.Quota(Empty.Quota); return; }
        if (cfg.Accounts.Length == 0) { hub.Quota(new("empty", null, "在本机设置中选择关注账号", [])); return; }
        var old = hub.Snapshot().Quota;
        try
        {
            using var listRequest = CpampRequest(HttpMethod.Get, "/v0/management/auth-files", cfg);
            using var listResponse = await client.SendAsync(listRequest, token);
            if (listResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { hub.Quota(old with { Status = "unauthorized", Message = "CPAMP 鉴权失败，请检查本机凭据" }); return; }
            listResponse.EnsureSuccessStatusCode();
            using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(token));
            var listRoot = QuotaAdapter.Unwrap(listDoc.RootElement);
            var files = listRoot.GetProperty("files").EnumerateArray().ToArray();
            var accountsList = new List<QuotaAccount>();
            bool snapshotFailed = false, databaseError = false, unsupported = false;
            foreach (var selection in cfg.Accounts)
            {
                token.ThrowIfCancellationRequested();
                var a = selection;
                var cachedHeader = new QuotaAccount(a.Id, a.Alias, a.Provider, "", []);
                var snapshotAccount = new QuotaAccount(a.Id, a.Alias, a.Provider, "", []);
                bool snapshotAvailable = false;
                try
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                    budget.CancelAfter(TimeSpan.FromSeconds(20));
                    var accountToken = budget.Token;
                    var file = files.FirstOrDefault(f => QuotaAdapter.Text(f, "name") == selection.AuthFile && QuotaAdapter.Text(f, "provider", QuotaAdapter.Text(f, "type")).ToLowerInvariant() == selection.Provider);
                    a = file.ValueKind == JsonValueKind.Object ? CachedHeaderAdapter.Identity(file, selection) : selection;
                    cachedHeader = file.ValueKind == JsonValueKind.Object ? CachedHeaderAdapter.ParseCodex(file, a, DateTimeOffset.UtcNow) : cachedHeader;
                    // Isolate each account: one damaged database bucket must not hide other accounts.
                    using var req = CpampRequest(HttpMethod.Post, "/v0/management/quota-snapshots/query", cfg);
                    req.Content = JsonContent.Create(new { accounts = new[] { new { row_key = a.Id, provider = a.Provider, account = new { auth_file_snapshot = a.AuthFile, auth_index = a.AuthIndex, auth_account_id_snapshot = a.AccountId, auth_provider_snapshot = a.Provider, account_snapshot = a.AccountSnapshot, auth_label_snapshot = a.AuthLabel, auth_project_id_snapshot = a.ProjectId, source = a.AuthFile } } }, include_inactive = false });
                    using var response = await client.SendAsync(req, accountToken);
                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(accountToken));
                        var classified = await quotaClassifier.Classify(doc.RootElement, a, DateTimeOffset.UtcNow, async (query, ct) => {
                            using var evidenceRequest = CpampRequest(HttpMethod.Post, "/v0/management/monitoring/analytics", cfg);
                            evidenceRequest.Content = JsonContent.Create(query);
                            using var evidenceResponse = await client.SendAsync(evidenceRequest, ct);
                            evidenceResponse.EnsureSuccessStatusCode();
                            using var evidence = JsonDocument.Parse(await evidenceResponse.Content.ReadAsStringAsync(ct));
                            return evidence.RootElement.Clone();
                        }, accountToken);
                        snapshotAccount = QuotaAdapter.Parse(classified, [a], DateTimeOffset.UtcNow).FirstOrDefault() ?? snapshotAccount;
                        snapshotAvailable = true;
                    }
                    else
                    {
                        snapshotFailed = true;
                        unsupported |= response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;
                        // Recognize this known production failure without exposing arbitrary response bodies.
                        var body = await response.Content.ReadAsStringAsync(accountToken);
                        databaseError |= body.Contains("database disk image is malformed", StringComparison.OrdinalIgnoreCase);
                        var previous = old.Accounts.FirstOrDefault(account => account.Id == a.Id);
                        if (previous is not null) snapshotAccount = previous with { Windows = previous.Windows.Select(w => w with { Stale = true }).ToArray(), ResetCredits = previous.ResetCredits is { } credits ? credits with { Stale = true } : null };
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) when (SourceFailure(e))
                {
                    snapshotFailed = true;
                    var previous = old.Accounts.FirstOrDefault(account => account.Id == a.Id);
                    if (previous is not null) snapshotAccount = previous with { Alias = a.Alias, Windows = previous.Windows.Select(w => w with { Stale = true }).ToArray(), ResetCredits = previous.ResetCredits is { } credits ? credits with { Stale = true } : null };
                }
                accountsList.Add(CachedHeaderAdapter.Merge(snapshotAccount, cachedHeader, snapshotAvailable));
            }
            var accounts = accountsList.ToArray();
            var any = accounts.Any(a => a.Windows.Length > 0);
            var stale = accounts.SelectMany(a => a.Windows).Any(w => w.Stale);
            var message = databaseError ? "CPAMP 配额数据库读取异常；使用已有响应头证据，缺失窗口等待快照" : unsupported ? "快照接口不可用；使用已有响应头证据" : snapshotFailed ? "部分账号快照读取失败；保留已有证据" : !any ? "暂无额度快照；可在本机设置中立即刷新" : "读取额度快照与响应头证据";
            var data = new QuotaData(!any ? "empty" : stale ? "stale" : "ok", DateTimeOffset.UtcNow, message, accounts);
            if (cfg != store.Config) return;
            hub.Quota(data); store.WriteCache("quota-cache", data);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (SourceFailure(e))
        { if (cfg == store.Config) hub.Quota(old with { Status = old.Accounts.Length > 0 ? "stale" : "offline", Message = "CPAMP 暂不可用，保留上次快照", Accounts = old.Accounts.Select(a => a with { Windows = a.Windows.Select(w => w with { Stale = true }).ToArray(), ResetCredits = a.ResetCredits is { } credits ? credits with { Stale = true } : null }).ToArray() }); }
    }
    private async Task UpdateWeather(CancellationToken token)
    {
        var cfg = store.Config;
        var city = cfg.City;
        var old = hub.Snapshot().Weather;
        try
        {
            var url = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={city.Latitude}&longitude={city.Longitude}&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&hourly=temperature_2m,precipitation_probability,precipitation,weather_code&timezone={Uri.EscapeDataString(city.Timezone)}&timeformat=unixtime&forecast_days=2");
            using var response = await client.GetAsync(url, token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var data = WeatherAdapter.Parse(doc.RootElement, city, DateTimeOffset.UtcNow);
            if (cfg != store.Config) return;
            hub.Weather(data); store.WriteCache("weather-cache", data);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (SourceFailure(e))
        { if (cfg == store.Config) hub.Weather(old with { Status = old.FetchedAt is null ? "offline" : "stale", Message = "天气暂不可用，保留上次数据" }); }
    }
}
