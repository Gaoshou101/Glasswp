using System.Net;
using System.Text.Json;
using WallpaperBridge;

static class AntigravitySubscriptionChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string? Plan(string json) { using var doc = JsonDocument.Parse(json); return AntigravitySubscriptionReader.ParsePlan(doc.RootElement); }
        check(Plan("""{"currentTier":{"id":"free-tier","name":"Antigravity"},"paidTier":{"id":"g1-pro-tier","name":"Google AI Pro"}}""") == "Google AI Pro", "Antigravity paid Pro overrides free current tier");
        check(Plan("""{"current_tier":{"id":"standard-tier","name":"Antigravity"},"paid_tier":{"id":"g1-pro-tier"}}""") == "Google AI Pro", "snake case tiers and known ID fallback preserve paid subscription");
        check(Plan("""{"paidTier":{"id":"g1-ultra-tier"}}""") == "Google AI Ultra" && Plan("""{"paidTier":{"id":"g1-ultra-lite-tier"}}""") == "Google AI Ultra Lite", "known Ultra tiers stay distinct");
        check(Plan("""{"currentTier":{"id":"free-tier","name":"Antigravity"},"paidTier":{}}""") == "Antigravity", "empty paid tier uses current tier");
        check(Plan("""{"allowedTiers":[{"id":"g1-pro-tier","name":"Google AI Pro"}]}""") is null, "offered subscription tiers never imply a purchased plan");
        check(Plan("""{"paidTier":{"id":"future-tier"}}""") is null && Plan("null") is null, "unknown and malformed subscription data remain unknown");

        var account = new AccountSelection("agi", "Example", "antigravity", "agi.json", "index-1", "");
        var cfg = BridgeConfig.Default with { CpampUrl = "https://example.test", ManagementKey = "test-only", Accounts = [account] };
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        var code = 200;
        var failFirstEndpoint = false;
        var payload = """{"currentTier":{"id":"free-tier"},"paidTier":{"id":"g1-pro-tier","name":"Google AI Pro"}}""";
        using var http = new HttpClient(new StubHttpHandler(async (request, token) => {
            calls++;
            check(request.RequestUri!.AbsolutePath == "/v0/management/api-call" && request.Headers.Authorization?.Parameter == "test-only", "subscription uses existing CPAMP authentication only");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var query = body.RootElement;
            using var data = JsonDocument.Parse(query.GetProperty("data").GetString()!);
            check(query.GetProperty("authIndex").GetString() == account.AuthIndex && query.GetProperty("header").GetProperty("Authorization").GetString() == "Bearer $TOKEN$" && data.RootElement.GetProperty("metadata").GetProperty("ideType").GetString() == "ANTIGRAVITY", "subscription uses current account and CPA token substitution");
            return StubHttpHandler.Json(new { status_code = failFirstEndpoint && query.GetProperty("url").GetString()!.Contains("daily-") ? 503 : code, body = payload });
        }));
        var reader = new AntigravitySubscriptionReader(http, () => now);
        check(await reader.Read(cfg, account, default) == "Google AI Pro" && calls == 1, "real subscription label reaches the reader");
        check(await reader.Read(cfg, account, default) == "Google AI Pro" && calls == 1, "repeated snapshots reuse the one hour subscription cache");
        code = 429; now = now.AddHours(1);
        check(await reader.Read(cfg, account, default) == "Google AI Pro" && calls == 2, "429 stops endpoint fallback and retains recent known plan");
        now = now.AddMinutes(9);
        check(await reader.Read(cfg, account, default) == "Google AI Pro" && calls == 2, "failed lookup waits ten minutes before retrying");
        now = now.AddDays(1);
        check(await reader.Read(cfg, account, default) is null && calls == 3, "failed subscription evidence expires after one day");
        code = 200; failFirstEndpoint = true; now = now.AddMinutes(10);
        check(await reader.Read(cfg, account, default) == "Google AI Pro" && calls == 5, "unavailable daily endpoint falls back to production endpoint");
        failFirstEndpoint = false; payload = "{}";
        check(await reader.Read(cfg with { CpampUrl = "https://other.example.test" }, account, default) is null && calls == 6, "source change clears cached subscription and empty response does not invent Pro");
        check(await reader.Read(cfg, account with { Provider = "codex" }, default) is null && calls == 6, "Codex never triggers an Antigravity subscription request");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancelledReader = new AntigravitySubscriptionReader(http);
        var propagated = false;
        try { await cancelledReader.Read(cfg, account, cancelled.Token); } catch (OperationCanceledException) { propagated = true; }
        check(propagated, "host cancellation is propagated instead of cached as provider failure");

        var root = Path.Combine(Path.GetTempPath(), "wallpaper-subscription-" + Guid.NewGuid());
        try {
            var store = new PrivateStore(root); store.Save(cfg);
            var hub = new DataHub(store);
            var subscriptionCalls = 0;
            using var remote = new RemoteSources(store, hub, new StubHttpHandler(async (request, token) => {
                if (request.RequestUri!.AbsolutePath.EndsWith("auth-files")) return StubHttpHandler.Json(new { files = new[] { new { name = "agi.json", provider = "antigravity", auth_index = "current-index" } } });
                if (request.RequestUri.AbsolutePath.EndsWith("api-call")) {
                    subscriptionCalls++;
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    check(body.RootElement.GetProperty("authIndex").GetString() == "current-index", "snapshot integration queries the newly discovered identity");
                    return StubHttpHandler.Json(new { status_code = 200, body = """{"paidTier":{"id":"g1-pro-tier","name":"Google AI Pro"}}""" });
                }
                return StubHttpHandler.Json(new { items = new[] { new { row_key = "agi", windows = new[] { new { provider_window_id = "gemini:weekly", model_scope_key = "gemini", remaining_percent = 73, observed_at_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } } } } });
            }));
            await remote.ReadQuotaOnce(default); await remote.ReadQuotaOnce(default);
            var data = hub.Snapshot().Quota;
            check(data.Accounts.Single().Plan == "Google AI Pro" && data.Accounts[0].Windows.Single().RemainingPercent == 73 && subscriptionCalls == 1, "subscription enriches shared snapshot without changing quota or repeating provider calls");
            using var failed = new RemoteSources(store, hub, new StubHttpHandler((request, _) => {
                if (request.RequestUri!.AbsolutePath.EndsWith("auth-files")) return Task.FromResult(StubHttpHandler.Json(new { files = new[] { new { name = "agi.json", provider = "antigravity", auth_index = "current-index" } } }));
                if (request.RequestUri.AbsolutePath.EndsWith("api-call")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                return Task.FromResult(StubHttpHandler.Json(new { items = new[] { new { row_key = "agi", windows = new[] { new { provider_window_id = "gemini:weekly", remaining_percent = 73, observed_at_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } } } } }));
            }));
            await failed.ReadQuotaOnce(default);
            data = hub.Snapshot().Quota;
            check(data.Accounts.Single().Plan == "antigravity" && data.Accounts[0].Windows.Single().RemainingPercent == 73 && data.Status == "ok", "subscription outage falls back to antigravity without marking healthy quota as failed");
        }
        finally { Directory.Delete(root, true); }
    }
}
