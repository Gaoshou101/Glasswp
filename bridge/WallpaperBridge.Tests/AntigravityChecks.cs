using System.Net;
using System.Text.Json;
using WallpaperBridge;

static class AntigravityChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var observed = DateTimeOffset.Parse("2026-10-22T15:00:00Z");
        using var quota = JsonDocument.Parse("""
        {"groups":[{"displayName":"Claude and GPT models","buckets":[
          {"bucketId":"3p-weekly","window":"weekly","remainingFraction":1,"resetTime":"2026-10-29T15:00:00Z"},
          {"bucketId":"3p-5h","window":"5h","remainingFraction":0,"resetTime":"2026-10-22T20:00:00Z"}]},
          {"display_name":"Gemini models","buckets":[
          {"bucket_id":"gemini-5h","window":"5h","remaining_fraction":0.35},
          {"bucket_id":"gemini-weekly","window":"weekly","remaining_fraction":0.5,"reset_time":"2026-10-29T15:00:00Z"}]}]}
        """);
        var windows = AntigravityQuotaAdapter.Windows(quota.RootElement, observed);
        check(windows.Length == 4 && (double)windows[0]["remaining_percent"]! == 0, "Antigravity retains all four buckets, including exhausted zero quota");
        check((string)windows[0]["provider_window_id"]! == "claude-and-gpt-models:3p-5h" && (string)windows[0]["model_scope_key"]! == "claude_gpt", "Antigravity IDs and scopes match existing CPAMP evidence");
        check((string)windows[0]["container_provider_window_id"]! == "claude-and-gpt-models:3p-weekly" && (long)windows[0]["cycle_end_ms"]! == observed.AddHours(5).ToUnixTimeMilliseconds(), "Antigravity short and weekly windows retain exact boundaries and relationship");
        check((string)windows[1]["boundary_accuracy"]! == "unknown" && !windows[1].ContainsKey("cycle_end_ms"), "Antigravity missing reset remains unknown");
        foreach (var invalid in new[] { "{}", "{\"groups\":[]}", "{\"groups\":[{\"displayName\":\"Gemini models\",\"buckets\":[{\"bucketId\":\"a\",\"remainingFraction\":null}]}]}" })
        {
            using var malformed = JsonDocument.Parse(invalid);
            var rejected = false;
            try { AntigravityQuotaAdapter.Windows(malformed.RootElement, observed); } catch (JsonException) { rejected = true; }
            check(rejected, "unreadable Antigravity inventory cannot overwrite existing evidence");
        }
        using var metadata = JsonDocument.Parse("""{"metadata":{"projectId":"test-project"}}""");
        check(AntigravityQuotaAdapter.Project(metadata.RootElement) == "test-project", "Antigravity resolves project metadata without downloading credentials");
        using var models = JsonDocument.Parse("""
        {"tabModelIds":["gemini-tab"],"models":{
          "gemini-tab":{"quotaInfo":{"remainingFraction":0}},
          "gemini-pro":{"quotaInfo":{"remainingFraction":0.4,"resetTime":"2026-10-22T20:00:00Z"}},
          "gemini-flash":{"quota_info":{"remaining_fraction":"0.4","reset_time":"2026-10-22T21:00:00Z"}},
          "gemini-missing":{"quotaInfo":{"resetTime":"2026-10-22T20:00:00Z"}},
          "claude-sonnet":{"quotaInfo":{"remainingFraction":0,"resetTime":"2026-10-22T20:00:00Z"}},
          "gpt-oss":{"quotaInfo":{"remainingFraction":1}}}}
        """);
        var shared = AntigravityQuotaAdapter.ModelWindows(models.RootElement, observed);
        var gemini = shared.Single(w => (string)w["model_scope_key"]! == "gemini");
        check(shared.Length == 2 && (double)gemini["remaining_percent"]! == 40, "model quota groups use the conservative balance, exclude tab models and ignore missing balances");
        check((long)gemini["cycle_end_ms"]! == observed.AddHours(6).ToUnixTimeMilliseconds() && !gemini.ContainsKey("duration_seconds") && (string)gemini["window_mode"]! == "unknown", "shared Gemini quota preserves reset without inventing a five-hour or weekly duration");
        check((double)shared.Single(w => (string)w["model_scope_key"]! == "claude_gpt")["remaining_percent"]! == 0, "shared model inventory retains a real exhausted balance");
        using var noBalance = JsonDocument.Parse("""{"models":{"gemini-pro":{"quotaInfo":{"resetTime":"2026-10-22T20:00:00Z"}}}}""");
        var noBalanceRejected = false;
        try { AntigravityQuotaAdapter.ModelWindows(noBalance.RootElement, observed); } catch (JsonException) { noBalanceRejected = true; }
        check(noBalanceRejected, "a reset timestamp without a balance never becomes zero or a fresh quota");
        using var parsedShared = JsonDocument.Parse(JsonSerializer.Serialize(new { items = new[] { new { row_key = "shared", windows = shared } } }));
        var displayedShared = QuotaAdapter.Parse(parsedShared.RootElement, [new("shared", "Example", "antigravity", "shared.json", "index", "")], observed);
        check(displayedShared[0].Windows.Any(w => w.Label == "Gemini · 共享额度" && w.DurationSeconds is null), "shared quota has an honest readable label");
        using var argon = JsonDocument.Parse("""{"groups":[{"displayName":"Gemini 4 Argon","buckets":[{"bucketId":"gemini-argon-wtu-weekly","window":"weekly","remainingFraction":0.63019174}]}]}""");
        var argonWindows = AntigravityQuotaAdapter.Windows(argon.RootElement, observed);
        using var argonSnapshot = JsonDocument.Parse(JsonSerializer.Serialize(new { items = new[] { new { row_key = "argon", windows = argonWindows } } }));
        check(QuotaAdapter.Parse(argonSnapshot.RootElement, [new("argon", "Example", "antigravity", "a.json", "i", "")], observed)[0].Windows.Single().Label == "Gemini 4 Argon · 周额度", "Argon weekly retains its distinct model identity");
        var root = Path.Combine(Path.GetTempPath(), "wallpaper-antigravity-" + Guid.NewGuid());
        try
        {
            var store = new PrivateStore(root);
            var selection = new AccountSelection("agi", "Antigravity", "antigravity", "agi.json", "stale-index", "");
            store.Save(store.Config with { ManagementKey = "test-only", Accounts = [selection] });
            var calls = 0; var writes = 0; var mode = "fallback";
            using var http = new HttpClient(new StubHttpHandler(async (request, token) => {
                if (request.RequestUri!.AbsolutePath.EndsWith("auth-files"))
                    return StubHttpHandler.Json(new { files = new[] { new { name = "agi.json", provider = "antigravity", auth_index = "current-index", project_id = "test-project" } } });
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                if (request.RequestUri.AbsolutePath.EndsWith("api-call"))
                {
                    calls++;
                    var value = body.RootElement;
                    using var project = JsonDocument.Parse(value.GetProperty("data").GetString()!);
                    check(value.GetProperty("authIndex").GetString() == "current-index" && value.GetProperty("method").GetString() == "POST" && value.GetProperty("header").GetProperty("Authorization").GetString() == "Bearer $TOKEN$" && project.RootElement.GetProperty("project").GetString() == "test-project", "Antigravity uses current identity, project and CPA token substitution");
                    check(value.GetProperty("header").GetProperty("User-Agent").GetString() == "antigravity/hub/2.23.0 darwin/arm64", "modern hub client requests full Gemini and Argon inventory");
                    if (mode == "limited") return StubHttpHandler.Json(new { status_code = 429, body = "private body" });
                    if (mode == "fallback" && calls == 1) return StubHttpHandler.Json(new { status_code = 404, body = "private body" });
                    return StubHttpHandler.Json(new { status_code = 200, body = quota.RootElement.GetRawText() });
                }
                writes++;
                var entry = body.RootElement.GetProperty("entries")[0];
                check(entry.GetProperty("provider").GetString() == "antigravity" && entry.GetProperty("account").GetProperty("auth_project_id_snapshot").GetString() == "test-project" && entry.GetProperty("windows").GetArrayLength() == 4, "Antigravity writes real observations with current project and four windows");
                check(entry.GetProperty("observation").GetProperty("inventory_mode").GetString() == "complete" && entry.GetProperty("observation").GetProperty("inventory_scope_key").GetString() == "antigravity:quota-windows", "complete summary retires old shared windows in the server inventory");
                return StubHttpHandler.Json(new { items = new[] { new { row_key = "agi", inserted_count = 4 } } });
            }));
            var clock = observed;
            var refresher = new UpstreamQuotaRefresher(store, http, () => clock);
            check(await refresher.Tick(CancellationToken.None) && refresher.Report().State.Status == "ok" && calls == 2 && writes == 1, "automatic Antigravity refresh falls back on unavailable endpoint and writes once");
            check(!await refresher.Tick(CancellationToken.None), "Antigravity shares the existing automatic schedule without repeat polling");
            mode = "limited"; calls = 0; clock = clock.AddMinutes(10);
            check(await refresher.Tick(CancellationToken.None) && refresher.Report().State.Status == "failed" && calls == 1 && writes == 1 && !JsonSerializer.Serialize(refresher.Report()).Contains("private body"), "Antigravity 429 stops fallback immediately and preserves prior evidence without leaking raw errors");
            check(!await refresher.Tick(CancellationToken.None), "failed Antigravity refresh waits for its scheduled retry");

            var supplementStore = new PrivateStore(Path.Combine(root, "supplement"));
            supplementStore.Save(store.Config);
            var modelCalls = 0; var summaryCalls = 0; var supplementalWrites = 0; var limited = false; var summaryUnavailable = false;
            using var partial = JsonDocument.Parse("""
            {"groups":[{"displayName":"Claude and GPT models","buckets":[
              {"bucketId":"3p-5h","window":"5h","remainingFraction":1},
              {"bucketId":"3p-weekly","window":"weekly","remainingFraction":1}]}]}
            """);
            using var supplementHttp = new HttpClient(new StubHttpHandler(async (request, token) => {
                if (request.RequestUri!.AbsolutePath.EndsWith("auth-files"))
                    return StubHttpHandler.Json(new { files = new[] { new { name = "agi.json", provider = "antigravity", auth_index = "current-index", project_id = "test-project" } } });
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                if (request.RequestUri.AbsolutePath.EndsWith("api-call")) {
                    if (body.RootElement.GetProperty("url").GetString()!.EndsWith("fetchAvailableModels")) {
                        modelCalls++;
                        clock = clock.AddSeconds(2);
                        return limited ? StubHttpHandler.Json(new { status_code = 429, body = "private diagnostic" }) : StubHttpHandler.Json(new { status_code = 200, body = models.RootElement.GetRawText() });
                    }
                    summaryCalls++;
                    if (summaryUnavailable) return StubHttpHandler.Json(new { status_code = 503, body = "private diagnostic" });
                    return StubHttpHandler.Json(new { status_code = 200, body = partial.RootElement.GetRawText() });
                }
                supplementalWrites++;
                var entry = body.RootElement.GetProperty("entries")[0];
                var windows = entry.GetProperty("windows");
                check(windows.EnumerateArray().Select(w => w.GetProperty("observed_at_ms").GetInt64()).Distinct().Count() == 1, "combined summary and model requests share one CPAMP observation despite elapsed query time");
                check(windows.GetArrayLength() == 2 && entry.GetProperty("observation").GetProperty("inventory_mode").GetString() == (summaryUnavailable ? "partial" : "complete"), "summary is authoritative; failed summary fallback cannot remove precise inventory");
                return StubHttpHandler.Json(new { items = new[] { new { row_key = "agi", inserted_count = windows.GetArrayLength() } } });
            }));
            var supplementalRefresh = new UpstreamQuotaRefresher(supplementStore, supplementHttp, () => clock);
            check(await supplementalRefresh.Tick(CancellationToken.None) && summaryCalls == 1 && modelCalls == 0 && supplementalWrites == 1 && supplementalRefresh.Report().State.Status == "ok", "readable summary never invents extra shared quota windows");
            limited = true; clock = clock.AddMinutes(10); modelCalls = summaryCalls = 0;
            check(await supplementalRefresh.Tick(CancellationToken.None) && summaryCalls == 1 && modelCalls == 0 && supplementalWrites == 2, "repeat refresh keeps only current summary inventory");
            limited = false; summaryUnavailable = true; clock = clock.AddMinutes(10); modelCalls = summaryCalls = 0;
            check(await supplementalRefresh.Tick(CancellationToken.None) && summaryCalls == 3 && modelCalls == 1 && supplementalWrites == 3, "all summary failures permit partial model fallback without retiring absent precise windows");
            limited = true; clock = clock.AddMinutes(10); modelCalls = summaryCalls = 0;
            check(await supplementalRefresh.Tick(CancellationToken.None) && summaryCalls == 3 && modelCalls == 1 && supplementalWrites == 3 && !JsonSerializer.Serialize(supplementalRefresh.Report()).Contains("private diagnostic"), "fallback 429 preserves server inventory without writing a deletion");
        }
        finally { Directory.Delete(root, true); }
    }
}
