using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.FileProviders;
using WallpaperBridge;

var argsList = args.ToList();
string? Option(string key) { var i = argsList.IndexOf(key); return i >= 0 && i + 1 < argsList.Count ? argsList[i + 1] : null; }
var store = new PrivateStore(Option("--data-dir"));
var port = int.TryParse(Option("--port"), out var specified) ? specified : store.Config.Port;
if (port is < 1024 or > 65535) throw new ArgumentException("Invalid port");
var builder = WebApplication.CreateBuilder(args);
// Never log request headers, upstream responses, config, or URLs containing secrets.
builder.Logging.ClearProviders();
builder.WebHost.UseKestrel(o => { o.Listen(IPAddress.Loopback, port); o.Limits.MaxRequestBodySize = 64 * 1024; });
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<DataHub>();
builder.Services.AddSingleton<DisplayTimeSync>();
builder.Services.AddSingleton<AccountActivityReader>();
builder.Services.AddSingleton<DesktopInputGuard>();
builder.Services.AddSingleton<DesktopWindowEvents>();
builder.Services.AddHostedService(p => p.GetRequiredService<DesktopWindowEvents>());
builder.Services.AddSingleton<HardwareSampler>();
builder.Services.AddHostedService(p => p.GetRequiredService<HardwareSampler>());
builder.Services.AddSingleton<RemoteSources>();
builder.Services.AddHostedService(p => p.GetRequiredService<RemoteSources>());
builder.Services.AddSingleton<AiQuotaSources>();
builder.Services.AddHostedService(p => p.GetRequiredService<AiQuotaSources>());
var app = builder.Build();
var sessions = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();
var candidates = new System.Collections.Concurrent.ConcurrentDictionary<string, AccountSelection>();
object? lastClient = null;
DateTimeOffset? lastSnapshotRead = null;

bool SameOrigin(HttpContext c) => c.Request.Headers.Origin.ToString() == $"http://{c.Request.Host}";
bool ReadAuthorized(HttpContext c)
{
    var supplied = c.Request.Headers["X-Wallpaper-Token"].ToString();
    return supplied.Length == store.ReadToken.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(store.ReadToken));
}
bool AdminAuthorized(HttpContext c) => SameOrigin(c) && sessions.TryGetValue(c.Request.Headers["X-Wallpaper-Admin"].ToString(), out var expiry) && expiry > DateTimeOffset.UtcNow;
app.Use(async (context, next) =>
{
    var c = context;
    if (c.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]")) { c.Response.StatusCode = 403; return; }
    c.Response.Headers.CacheControl = "no-store";
    c.Response.Headers.XContentTypeOptions = "nosniff";
    c.Response.Headers["Referrer-Policy"] = "no-referrer";
    c.Response.Headers["X-Frame-Options"] = "DENY";
    var origin = c.Request.Headers.Origin.ToString();
    var readRoute = c.Request.Path.StartsWithSegments("/v1") && !c.Request.Path.StartsWithSegments("/v1/setup");
    if (readRoute && origin.Length > 0)
    {
        var localOrigin = Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.Scheme == "http" && u.Host is "127.0.0.1" or "localhost" or "[::1]";
        if (origin != "null" && !localOrigin) { c.Response.StatusCode = 403; return; }
        c.Response.Headers.AccessControlAllowOrigin = origin;
        c.Response.Headers.Vary = "Origin";
        c.Response.Headers.AccessControlAllowHeaders = "Content-Type, X-Wallpaper-Token";
        c.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
        c.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }
    if (c.Request.Method == "OPTIONS" && readRoute) { c.Response.StatusCode = 204; return; }
    if (c.Request.Path.StartsWithSegments("/v1/setup") && c.Request.Path != "/v1/setup/session" && !AdminAuthorized(c)) { c.Response.StatusCode = 403; return; }
    if (readRoute && c.Request.Path != "/v1/health" && !ReadAuthorized(c)) { c.Response.StatusCode = 401; return; }
    try { await next(); }
    catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
    { c.Response.StatusCode = 400; await c.Response.WriteAsJsonAsync(new { message = "配置无效，请检查输入或连接状态" }); }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    { c.Response.StatusCode = 502; await c.Response.WriteAsJsonAsync(new { message = "远端请求失败，请检查地址、鉴权和实例版本" }); }
});
app.MapGet("/v1/health", (DesktopInputGuard input, DesktopWindowEvents windowEvents) => Results.Json(new { service = "portrait-wallpaper-bridge", version = "0.1.0", buildId = BuildIdentity.Id, schemaVersion = 1, desktopInputAvailable = input.Available, minimizeGuardAvailable = windowEvents.Available, closeGuardAvailable = windowEvents.CloseGuardAvailable,
    elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) }));
app.MapGet("/v1/snapshot", (DataHub hub) => { lastSnapshotRead = DateTimeOffset.UtcNow; return Results.Json(hub.Snapshot()); });
app.MapPost("/v1/desktop-input", async (DesktopInputPoint point, DesktopInputGuard input, CancellationToken token) => {
    // Let minimize/hide/destroy notifications from this same release reach the event thread.
    await Task.Delay(20, token);
    return Results.Json(new { allowed = input.Allows(point), available = input.Available });
});
app.MapGet("/v1/accounts/{id}/activity", async (string id, HttpRequest request, AccountActivityReader reader, DataHub hub, CancellationToken token) => {
    ActivityQuery query;
    try { query = ActivityQuery.Parse(request.Query); }
    catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
    if (hub.AiQuotaSnapshot().Accounts.Any(a => a.Id == id))
        return Results.Json(new AccountActivity(id, "unsupported", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            null, null, null, null, null, [], false, "此账号来自独立 AI 来源，未接入 CPAMP 请求记录"));
    return await reader.Read(id, token, query) is { } data ? Results.Json(data) : Results.NotFound(new { message = "账号已不在关注列表中" });
});
app.MapPost("/v1/client", (ClientReport report) =>
{
    lastClient = new { at = DateTimeOffset.UtcNow, buildId = new string((report.BuildId ?? "legacy").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.').Take(80).ToArray()), host = report.WallpaperEngine ? "wallpaper-engine" : "browser", width = Math.Clamp(report.Width, 0, 16384), height = Math.Clamp(report.Height, 0, 16384), fps = Math.Clamp(report.Fps, 0, 240), paused = report.Paused,
        materials = report.Materials?.Where(p => p.Key is "top" or "quota" or "hardware").ToDictionary(p => p.Key, p => p.Value.Take(3).Select(v => double.IsFinite(v) ? Math.Clamp(v, 0, 100) : 0).ToArray()) };
    return Results.NoContent();
});
// A display ticket may request only this fixed refresh; setup and credentials stay admin-only.
object WallpaperRefreshReport(RemoteSources remote, AiQuotaSources ai, DataHub hub) => new {
    canRefresh = (AiSourceSettings.UsesCpamp(store.Config) && !string.IsNullOrEmpty(store.Config.ManagementKey) && store.Config.Accounts.Length > 0) || store.Config.AiSources?.AnyEnabled == true,
    refresh = AiSourceSettings.UsesCpamp(store.Config) ? remote.RefreshStatus() : ai.RefreshStatus(), snapshotFetchedAt = hub.Snapshot().Quota.FetchedAt
};
app.MapGet("/v1/quota-refresh/status", (RemoteSources remote, AiQuotaSources ai, DataHub hub) => Results.Json(WallpaperRefreshReport(remote, ai, hub)));
app.MapPost("/v1/quota-refresh", (RemoteSources remote, AiQuotaSources ai, DataHub hub) => {
    if (AiSourceSettings.UsesCpamp(store.Config)) remote.QueueQuotaRefresh(); else ai.Queue();
    return Results.Json(WallpaperRefreshReport(remote, ai, hub));
});
app.MapPost("/v1/setup/session", (HttpContext c) =>
{
    if (!SameOrigin(c) || c.Request.Headers["X-Wallpaper-Setup"] != "1") return Results.StatusCode(403);
    foreach (var entry in sessions.Where(s => s.Value < DateTimeOffset.UtcNow)) sessions.TryRemove(entry.Key, out _);
    var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    sessions[session] = DateTimeOffset.UtcNow.AddHours(2);
    return Results.Json(new { session });
});
app.MapPost("/v1/setup/config/read", (RemoteSources remote, DesktopInputGuard input, DesktopWindowEvents windowEvents) => Results.Json(new {
    cpampUrl = store.Config.CpampUrl, managementConfigured = !string.IsNullOrEmpty(store.Config.ManagementKey),
    aiSources = new { source = (store.Config.AiSources ?? new()).EffectiveSource, codexEnabled = store.Config.AiSources?.CodexEnabled ?? false, claudeEnabled = store.Config.AiSources?.ClaudeEnabled ?? false,
        magpieEnabled = store.Config.AiSources?.MagpieEnabled ?? false, magpieUrl = store.Config.AiSources?.MagpieUrl ?? "http://127.0.0.1:3425", magpieKeyConfigured = !string.IsNullOrEmpty(store.Config.AiSources?.MagpieApiKey),
        localOptions = (store.Config.AiSources ?? new()).Options("local"), magpieOptions = (store.Config.AiSources ?? new()).Options("magpie"),
        cpaUrl = store.Config.AiSources?.CpaUrl ?? "", cpaKeyConfigured = !string.IsNullOrEmpty(store.Config.AiSources?.CpaKey), cpaApiVersion = store.Config.AiSources?.CpaApiVersion ?? "v0", cpaOptions = (store.Config.AiSources ?? new()).Options("cpa"),
        sub2apiUrl = store.Config.AiSources?.Sub2apiUrl ?? "", sub2apiKeyConfigured = !string.IsNullOrEmpty(store.Config.AiSources?.Sub2apiKey), sub2apiOptions = (store.Config.AiSources ?? new()).Options("sub2api") },
    city = store.Config.City, accounts = store.Config.Accounts.Select(a => new { a.Id, a.Alias, a.Provider }), sensors = store.Config.Sensors,
    clock = store.Config.Clock, appearance = (store.Config.Appearance ?? WallpaperAppearance.Default).Normalize(), weather = new { provider = (store.Config.Weather ?? new()).Provider, apiKeyConfigured = !string.IsNullOrEmpty(store.Config.Weather?.ApiKey), apiHost = store.Config.Weather?.ApiHost, cityCode = store.Config.Weather?.CityCode },
    quotaRefreshEnabled = store.Config.QuotaRefreshEnabled, quotaRefreshMinutes = store.Config.QuotaRefreshMinutes, quotaRefresh = remote.RefreshStatus(),
    bridgeUrl = $"http://127.0.0.1:{port}", readToken = store.ReadToken, diagnostics = new { lastClient, lastSnapshotRead, cacheFaults = store.CacheFaults, desktopInput = input.LastCheck, lastMinimize = windowEvents.LastMinimize, lastWindowDisappearance = windowEvents.LastWindowDisappearance }
}));
app.MapPost("/v1/setup/config", (SetupInput input) =>
{
    var cfg = store.Config;
    var urlChanged = input.CpampUrl is not null && input.CpampUrl.TrimEnd('/') != cfg.CpampUrl.TrimEnd('/');
    var sourceChanged = urlChanged || input.ManagementKey is not null;
    var selected = input.Accounts?.Select(a =>
    {
        var source = candidates.GetValueOrDefault(a.Id) ?? cfg.Accounts.FirstOrDefault(x => x.Id == a.Id) ?? throw new ArgumentException("请选择已发现的账号");
        return source with { Alias = string.IsNullOrWhiteSpace(a.Alias) ? source.Alias : a.Alias.Trim() };
    }).ToArray() ?? (sourceChanged ? [] : cfg.Accounts);
    store.Save(cfg with { CpampUrl = input.CpampUrl?.Trim().TrimEnd('/') ?? cfg.CpampUrl, ManagementKey = input.ClearManagementKey ? null : input.ManagementKey?.Trim() ?? (urlChanged ? null : cfg.ManagementKey),
        City = input.City ?? cfg.City, Accounts = selected, Sensors = input.Sensors ?? cfg.Sensors,
        QuotaRefreshEnabled = input.QuotaRefreshEnabled ?? cfg.QuotaRefreshEnabled, QuotaRefreshMinutes = input.QuotaRefreshMinutes ?? cfg.QuotaRefreshMinutes,
        Clock = input.Clock ?? cfg.Clock, Weather = input.Weather?.Apply(cfg.Weather) ?? cfg.Weather,
        AiSources = input.AiSources?.Apply(cfg.AiSources) ?? cfg.AiSources, Appearance = input.Appearance?.Normalize() ?? cfg.Appearance });
    if (sourceChanged) candidates.Clear();
    // Source revisions queue their own refresh. Display-only edits need no upstream request.
    return Results.Json(new { message = "已加密保存；数据将在数秒内更新" });
});
app.MapPost("/v1/setup/accounts", async (RemoteSources remote, CancellationToken ct) =>
{
    var accounts = await remote.DiscoverAccounts(ct);
    candidates.Clear(); foreach (var account in accounts) candidates[account.Id] = account;
    return Results.Json(new { accounts = accounts.Select(a => new { a.Id, a.Alias, a.Provider, label = a.AuthFile, selected = store.Config.Accounts.Any(s => s.Id == a.Id) }) });
});
app.MapPost("/v1/setup/cities", async (CityQuery query, RemoteSources remote, CancellationToken ct) => Results.Json(new { cities = await remote.SearchCities(query.Query, ct) }));
app.MapPost("/v1/setup/ai-sources/status", (DataHub hub) => Results.Json(hub.AiQuotaSnapshot()));
app.MapPost("/v1/setup/ai-sources/refresh-status", (AiQuotaSources ai) => Results.Json(ai.RefreshStatus()));
app.MapPost("/v1/setup/ai-sources/refresh", (AiQuotaSources ai) => { ai.Queue(); return Results.Json(new { message = "已请求读取 AI 来源；同一来源最短间隔 60 秒" }); });
app.MapPost("/v1/setup/time/sync", async (DisplayTimeSync sync, CancellationToken ct) => Results.Json(await sync.Measure(ct)));
app.MapPost("/v1/setup/sensors", (HardwareSampler sampler) => Results.Json(new { sensors = sampler.Inventory }));
app.MapPost("/v1/setup/refresh", (RemoteSources remote) => { remote.RefreshCachedSources(); return Results.Json(new { message = "正在重新读取缓存与天气；未触发 Provider 刷新" }); });
app.MapPost("/v1/setup/quota-refresh/status", (RemoteSources remote) => Results.Json(remote.RefreshStatus()));
app.MapPost("/v1/setup/quota-refresh", (RemoteSources remote) => Results.Json(new { message = remote.QueueQuotaRefresh() ? "已排队，正在向上游刷新用量" : "刷新正在进行，请稍候", refresh = remote.RefreshStatus() }));

var webRoot = Path.GetFullPath(Option("--web-root") ?? Path.Combine(AppContext.BaseDirectory, "wallpaper"));
if (Directory.Exists(webRoot))
{
    var provider = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
}
Console.WriteLine($"Portrait wallpaper bridge: http://127.0.0.1:{port}/admin/ (Ctrl+C to stop)");
if (Option("--probe") is { } probePath)
{
    await app.StartAsync();
    var probeDeadline = DateTimeOffset.UtcNow.AddSeconds(45);
    while (app.Services.GetRequiredService<DataHub>().Snapshot().Hardware.ObservedAt is null && DateTimeOffset.UtcNow < probeDeadline) await Task.Delay(500);
    await Task.Delay(2000);
    await File.WriteAllTextAsync(Path.GetFullPath(probePath), System.Text.Json.JsonSerializer.Serialize(new { hardware = app.Services.GetRequiredService<DataHub>().Snapshot().Hardware, sensors = app.Services.GetRequiredService<HardwareSampler>().Inventory }, PrivateStore.Json));
    await app.StopAsync();
    return;
}
await app.RunAsync();

public sealed record SetupInput(string? CpampUrl = null, string? ManagementKey = null, bool ClearManagementKey = false, City? City = null, SelectionInput[]? Accounts = null, SensorSelection? Sensors = null, bool? QuotaRefreshEnabled = null, int? QuotaRefreshMinutes = null, ClockSettings? Clock = null, WeatherSettingsInput? Weather = null, AiSourceSettingsInput? AiSources = null, WallpaperAppearance? Appearance = null);
public sealed record SelectionInput(string Id, string Alias);
public sealed record CityQuery(string Query);
public sealed record ClientReport(bool WallpaperEngine, int Width, int Height, double Fps, bool Paused, Dictionary<string, double[]>? Materials = null, string? BuildId = null);
