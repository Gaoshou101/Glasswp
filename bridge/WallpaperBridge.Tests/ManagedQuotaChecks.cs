using System.Net;
using System.Text.Json;
using WallpaperBridge;

internal static class ManagedQuotaChecks
{
    private static readonly string Codex = """{"plan_type":"plus","rate_limit":{"primary_window":{"limit_window_seconds":18000,"used_percent":25,"reset_after_seconds":3600}},"rate_limit_reset_credits":{"available_count":2}}""";
    public static async Task Run(Action<bool, string> check)
    {
        var cpa = new AiSourceSettings(Source: "cpa", CpaUrl: "https://cpa.example.test", CpaKey: "cpa-fixture-key");
        var sub = new AiSourceSettings(Source: "sub2api", Sub2apiUrl: "https://sub.example.test", Sub2apiKey: "sub-fixture-key");
        var both = new AiSourceSettingsInput(Sub2apiUrl: sub.Sub2apiUrl, Sub2apiKey: sub.Sub2apiKey, Sub2apiOptions: new(false, 17)).Apply(cpa);
        check(both.CpaKey == cpa.CpaKey && both.Sub2apiKey == sub.Sub2apiKey && both.Options("sub2api").RefreshMinutes == 17, "managed sources store independent secrets and schedules");
        check(new AiSourceSettingsInput(Source: "local").Apply(both).CpaKey == cpa.CpaKey && new AiSourceSettingsInput(ClearCpaKey: true).Apply(both).Sub2apiKey == sub.Sub2apiKey, "switching and clearing CPA never changes another source credential");
        var filtered = cpa with { CpaOptions = new(Accounts: [new("old", "old")]) };
        check(new AiSourceSettingsInput(CpaUrl: "https://new.example.test").Apply(filtered) is { CpaKey: null, CpaOptions.Accounts: null }, "CPA address change clears old credentials and account selections");
        check(!cpa.SameConnection(cpa with { CpaKey = "replacement" }) && !cpa.SameConnection(cpa with { CpaApiVersion = "v8" }) && cpa.SameConnection(filtered), "CPA evidence is bound to endpoint key and API version, not display settings");
        foreach (var invalid in new[] { cpa with { CpaUrl = "http://remote.test" }, cpa with { CpaUrl = "https://user:pass@remote.test" }, sub with { Sub2apiUrl = "https://remote.test/?key=x" }, sub with { Sub2apiKey = "x\ny" }, cpa with { CpaApiVersion = "auto-bad" } }) {
            var rejected = false; try { AiSourceSettings.Validate(invalid); } catch (ArgumentException) { rejected = true; }
            check(rejected, "managed source rejects unsafe URL credential or version");
        }
        using var cpaHandler = new Handler(async request => {
            check(request.Headers.Authorization?.Parameter == "cpa-fixture-key" && !request.Headers.Contains("x-api-key"), "CPA requests use only its management bearer key");
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth-files") || path.EndsWith("/credentials")) return Json("""{"files":[{"name":"hidden@example.test.json","auth_index":"idx","provider":"codex","id_token":{"chatgpt_account_id":"workspace","plan_type":"free"}},{"name":"plugin","auth_index":"plugin-idx","provider":"claude","quota_plugin_id":"test-plugin"}]}""");
            if (path.EndsWith("/plugins/test-plugin/quota")) return Json("""{"subscription":{"plan":"max"},"groups":[{"displayName":"Claude","buckets":[{"window":"5h","remainingFraction":0.6},{"window":"7d","remaining_fraction":0.3}]}]}""");
            check(path.EndsWith("/api-call"), "CPA direct reader never calls CPAMP snapshot APIs");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            check(body.RootElement.GetProperty("url").GetString() == "https://chatgpt.com/backend-api/wham/usage" && body.RootElement.GetProperty("authIndex").GetString() == "idx", "CPA sends fixed quota URL and freshly discovered auth index");
            check(body.RootElement.GetProperty("header").GetProperty("Authorization").GetString() == "Bearer $TOKEN$", "CPA performs OAuth replacement without local token download");
            return Json(JsonSerializer.Serialize(new { status_code = 200, body = Codex }));
        });
        using var cpaClient = new HttpClient(cpaHandler);
        var result = await new CpaQuotaReader(cpaClient).Read(cpa, CancellationToken.None);
        check(result.Status == "ok" && result.Accounts.Length == 2 && result.Accounts[0].Plan == "GPT Plus" && result.Accounts[0].Windows.Single().RemainingPercent == 75, "CPA direct Codex query maps actual plan and quota independently of CPAMP");
        check(result.Accounts[0].ResetCredits?.Available == 2 && result.Accounts[1].Windows.Select(w => w.RemainingPercent).SequenceEqual(new double?[] { 60, 30 }), "CPA reset credit and plugin fraction units are correct");
        check(!JsonSerializer.Serialize(result).Contains("hidden@example.test") && result.Accounts.All(a => a.Id.StartsWith("cpa:")), "CPA identities are source-namespaced and login filenames stay private");
        var v8 = await new CpaQuotaReader(cpaClient).Read(cpa with { CpaApiVersion = "v8" }, CancellationToken.None);
        check(v8.Accounts.Length == 2 && cpaHandler.Paths.Contains("/v8/management/credentials") && cpaHandler.Paths.Contains("/v8/management/requests/api-call"), "CPA v8 uses its distinct management routes");
        using var otherHandler = new Handler(async request => {
            if (request.Method == HttpMethod.Get) return Json("""{"files":[{"provider":"claude","name":"claude","auth_index":"claude-index"},{"provider":"antigravity","name":"ag","auth_index":"ag-index","project_id":"project"}]}""");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var url = body.RootElement.GetProperty("url").GetString()!;
            var payload = url.EndsWith("/usage") ? """{"five_hour":{"utilization":40}}""" :
                url.EndsWith("/profile") ? """{"organization":{"organization_type":"claude_max","rate_limit_tier":"default_claude_max_20x"}}""" :
                url.EndsWith(":loadCodeAssist") ? """{"paidTier":{"id":"g1-pro-tier","name":"Google AI Pro"}}""" :
                """{"groups":[{"displayName":"Gemini models","buckets":[{"bucketId":"gemini-5h","window":"5h","remainingFraction":0.4}]}]}""";
            return Json(JsonSerializer.Serialize(new { status_code = 200, body = payload }));
        });
        using var otherHttp = new HttpClient(otherHandler);
        var other = await new CpaQuotaReader(otherHttp).Read(cpa, CancellationToken.None);
        check(other.Accounts[0].Windows.Single().RemainingPercent == 60 && other.Accounts[1].Windows.Single().RemainingPercent == 40 && other.Accounts[1].Plan == "Google AI Pro", "CPA native Claude and Antigravity fallback queries preserve real quota and paid tier");

        var now = DateTimeOffset.UtcNow;
        using var subHandler = new Handler(request => {
            check(request.Headers.GetValues("x-api-key").Single() == "sub-fixture-key" && request.Headers.Authorization is null && request.Method == HttpMethod.Get, "Sub2API queries use its administrator key and GET only");
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/admin/accounts") return Task.FromResult(Json("""{"code":0,"data":{"total":3,"items":[{"id":1,"name":"Codex","platform":"openai","type":"oauth"},{"id":2,"name":"Claude","platform":"anthropic","type":"oauth"},{"id":3,"name":"API key","platform":"anthropic","type":"apikey","quota_limit":100,"quota_used":10}]}}"""));
            if (path == "/api/v1/admin/openai/accounts/1/quota") return Task.FromResult(Json("{\"code\":0,\"data\":" + Codex + "}"));
            check(path == "/api/v1/admin/accounts/2/usage" && request.RequestUri.Query == "?source=active", "Sub2API queries Claude usage, never Codex synthetic probe or resets");
            return Task.FromResult(Json(JsonSerializer.Serialize(new { code = 0, data = new { updated_at = now, five_hour = new { utilization = 90, resets_at = now.AddHours(1) } } })));
        });
        using var subClient = new HttpClient(subHandler);
        var subResult = await new Sub2apiQuotaReader(subClient).Read(sub, CancellationToken.None);
        check(subResult.Accounts.Length == 3 && subResult.Accounts[0].Plan == "GPT Plus" && subResult.Accounts[1].Windows.Single().RemainingPercent == 10 && subResult.Accounts[2].Windows.Length == 0, "Sub2API maps hosted OAuth quota and never treats site spending limits as provider balance");
        check(subResult.Accounts.All(a => a.Id.StartsWith("sub2api:")) && subResult.Status == "stale", "Sub2API source IDs and partial unknown quota are explicit");
        using var stale = JsonDocument.Parse("""{"updated_at":"2020-01-01T00:00:00Z","five_hour":{"utilization":0}}""");
        check(Sub2apiQuotaReader.ParseUsage(stale.RootElement, subResult.Accounts[1] with { Windows = [] }, now).Windows.Single() is { RemainingPercent: 100, Stale: true }, "Sub2API retains zero usage and stale observation time");
        using var failed = JsonDocument.Parse("""{"error_code":"unauthenticated","five_hour":{"utilization":0}}""");
        check(Sub2apiQuotaReader.ParseUsage(failed.RootElement, subResult.Accounts[1] with { Windows = [] }, now).Windows.Length == 0, "Sub2API error payload never becomes full remaining quota");
        using var groups = JsonDocument.Parse(JsonSerializer.Serialize(new { updated_at = now, subscription_tier = "PRO", antigravity_quota_groups = new[] { new { name = "Gemini", windows = new[] { new { window = "5h", utilization = 15 } } } }, antigravity_quota = new { duplicate = new { utilization = 15 } } }));
        var ag = Sub2apiQuotaReader.ParseUsage(groups.RootElement, new("x", "AG", "antigravity", "", []), now);
        check(ag.Windows.Length == 1 && ag.Windows[0].RemainingPercent == 85 && ag.Plan == "PRO", "Sub2API Antigravity group inventory is preferred over duplicate per-model buckets");
        using var badPlugin = JsonDocument.Parse("""{"groups":[{"buckets":[{"remainingFraction":50},{"remainingFraction":0},{"window":"unknown"}]}]}""");
        check(CpaQuotaReader.ParsePlugin(badPlugin.RootElement, result.Accounts[0], now).Windows.Single().RemainingPercent == 0, "CPA plugin invalid or missing fractions never invent percentage while zero is retained");

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Redirect, HttpStatusCode.TooManyRequests }) {
            using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("secret-server-body") }));
            using var http = new HttpClient(handler);
            var c = await new CpaQuotaReader(http).Read(cpa, CancellationToken.None);
            var s = await new Sub2apiQuotaReader(http).Read(sub, CancellationToken.None);
            check(c.Accounts.Length == 0 && s.Accounts.Length == 0 && !c.Message.Contains("secret") && handler.Paths.Count == 2, "managed auth/redirect/rate-limit failures do not follow or leak server response");
        }
        using var noRequest = new Handler(_ => throw new Exception("Unexpected network request")); using var emptyHttp = new HttpClient(noRequest);
        check((await new CpaQuotaReader(emptyHttp).Read(new(Source: "cpa"), CancellationToken.None)).Status == "unconfigured" &&
            (await new Sub2apiQuotaReader(emptyHttp).Read(new(Source: "sub2api"), CancellationToken.None)).Status == "unconfigured", "new managed sources require configuration before any request");
        using var paginated = new Handler(request => {
            var second = request.RequestUri!.Query.Contains("page=2&");
            var items = Enumerable.Range(second ? 101 : 1, second ? 1 : 100).Select(id => new { id, name = "fixture", platform = "unknown", type = "apikey" }).ToArray();
            return Task.FromResult(Json(JsonSerializer.Serialize(new { code = 0, data = new { total = 101, items } })));
        });
        using var pageHttp = new HttpClient(paginated);
        check((await new Sub2apiQuotaReader(pageHttp).Read(sub, CancellationToken.None)).Accounts.Length == 101 && paginated.Paths.Count == 2, "Sub2API discovers all pages without querying unsupported account types");

        var root = Path.Combine(Path.GetTempPath(), "wallpaper-managed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var store = new PrivateStore(Path.Combine(root, "private")); store.Save(store.Config with { AiSources = cpa });
            var hub = new DataHub(store);
            using var handler = new CancelHandler(); using var reader = new AiQuotaSources(store, hub, handler, Path.Combine(root, "empty-home"));
            await reader.StartAsync(CancellationToken.None);
            try {
                await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                store.Save(store.Config with { AiSources = sub with { Sub2apiOptions = new(false) } });
                await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(hub.Snapshot().Quota.Accounts.Length == 0 && reader.RefreshStatus().State.Status == "idle", "source switching cancels the old managed query and cannot publish its report into the new source");
                check(handler.Requests == 1, "disabled new source never contacts its endpoint during a switch");
            } finally { await reader.StopAsync(CancellationToken.None); }
        } finally { Directory.Delete(root, recursive: true); }
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler {
        public readonly List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath); return respond(request); }
    }
    private sealed class CancelHandler : HttpMessageHandler {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            Requests++; Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); throw new Exception("Unreachable"); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
    }
}
