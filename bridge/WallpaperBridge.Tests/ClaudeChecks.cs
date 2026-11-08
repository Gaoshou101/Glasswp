using System.Net;
using System.Text.Json;
using WallpaperBridge;

static class ClaudeChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var observed = DateTimeOffset.Parse("2026-11-08T03:00:00Z");
        var weeklySeconds = observed.AddDays(6).ToUnixTimeSeconds();
        var opusMs = observed.AddDays(5).ToUnixTimeMilliseconds();
        using var usage = JsonDocument.Parse("""
        {"five_hour":{"utilization":42,"resets_at":"2026-11-08T05:00:00Z"},
         "seven_day":{"utilization":"18.5","resets_at":__WEEKLY__},
         "seven_day_oauth_apps":null,
         "seven_day_opus":{"utilization":null,"resets_at":__OPUS__},
         "seven_day_sonnet":{"utilization":null,"resets_at":null},
         "seven_day_cowork":{"resets_at":"2026-11-14T00:00:00Z"},
         "limits":[
           {"kind":"weekly_scoped","scope":{"model":{"id":"claude-opus-4-1","display_name":"Opus  4.1"}},"percent":61,"resets_at":"2026-11-13T00:00:00Z","is_active":true},
           {"kind":"weeklyScoped","scope":{"model":{"display_name":"Opus 4.1"}},"percent":61,"resets_at":"2026-11-13T00:00:00Z","is_active":true},
           {"kind":"model_scoped","group":"weekly","scope":{"model":{"details":{"name":"Haiku Beta"}}},"utilization":5,"resetAt":"2026-11-12T00:00:00Z"},
           {"kind":"model_scoped","group":"session","scope":{"model":{"id":"ignored","name":"Ignored"}},"percent":99},
           {"kind":"session","percent":77,"resets_at":"2026-11-08T04:00:00Z"}],
         "extra_usage":{"is_enabled":true,"used_credits":1250,"monthly_limit":5000,"utilization":null}}
        """.Replace("__WEEKLY__", weeklySeconds.ToString()).Replace("__OPUS__", opusMs.ToString()));
        var windows = ClaudeQuotaAdapter.Windows(usage.RootElement, observed, "plan_max");
        string Id(Dictionary<string, object?> w) => (string)w["provider_window_id"]!;
        check(windows.Select(Id).SequenceEqual(new[] { "five-hour", "seven-day", "weekly-scoped-haiku%20beta", "weekly-scoped-id-claude-opus-4-1", "seven-day-opus", "extra-usage" }),
            "Claude follows CPAMP window order, inserts scoped weekly limits after seven_day and drops empty or absent windows");
        var fiveHour = windows[0];
        check((double)fiveHour["used_percent"]! == 42 && (double)fiveHour["remaining_percent"]! == 58 && (string)fiveHour["window_kind"]! == "five_hour" && (long)fiveHour["duration_seconds"]! == 18000
            && (long)fiveHour["cycle_end_ms"]! == observed.AddHours(2).ToUnixTimeMilliseconds() && (string)fiveHour["boundary_accuracy"]! == "exact" && (string)fiveHour["window_mode"]! == "fixed",
            "a top-level five-hour window wins over the limits fallback and keeps its exact reset");
        check((string)fiveHour["model_scope_kind"]! == "all" && !fiveHour.ContainsKey("model_scope_key") && windows.All(w => (string)w["plan_type"]! == "plan_max" && (long)w["observed_at_ms"]! == observed.ToUnixTimeMilliseconds()),
            "top-level Claude windows use CPAMP's account-wide scope, one observation time and the profile plan");
        check((double)windows[1]["used_percent"]! == 18.5 && (long)windows[1]["cycle_end_ms"]! == weeklySeconds * 1000 && (long)windows[4]["cycle_end_ms"]! == opusMs && !windows[4].ContainsKey("used_percent"),
            "numeric resets below 1e12 are seconds, larger values are milliseconds, and a reset without utilization stays unknown");
        var opus = windows[3];
        check((string)opus["model_scope_kind"]! == "models" && ((string[])opus["model_ids"]!).SequenceEqual(new[] { "claude-opus-4-1" }) && (double)opus["used_percent"]! == 61 && (string)opus["window_mode"]! == "fixed",
            "an identified scoped weekly limit keeps its model and the equivalent label-only duplicate is merged");
        var haiku = windows[2];
        check((string)haiku["model_scope_kind"]! == "feature" && (string)haiku["model_scope_key"]! == "scope_unknown" && !haiku.ContainsKey("model_ids") && (string)haiku["window_mode"]! == "unknown"
            && (string)haiku["boundary_accuracy"]! == "unknown" && haiku.ContainsKey("cycle_end_ms"),
            "a scoped weekly limit without a model ID is persisted as an incomplete scope, never as account-wide");
        var extra = windows[5];
        check((string)extra["window_kind"]! == "billing" && (string)extra["window_mode"]! == "non_window" && (double)extra["used_percent"]! == 25 && (double)extra["used_value"]! == 12.5
            && (double)extra["limit_value"]! == 50 && (string)extra["quota_unit"]! == "USD" && !extra.ContainsKey("cycle_end_ms"),
            "enabled extra usage converts cents to dollars and derives its percentage when utilization is absent");

        using var fallback = JsonDocument.Parse("""
        {"five_hour":null,"limits":[
          {"kind":"session","percent":90,"resets_at":"2026-11-08T08:00:00Z","is_active":false},
          {"kind":"session","percent":30,"resets_at":"2026-11-08T04:00:00Z"},
          {"kind":"session","group":"session","percent":40,"resets_at":"2026-11-08T05:00:00Z"},
          {"kind":"weekly","percent":10,"resets_at":"2026-11-14T00:00:00Z"},
          {"kind":"weekly_all","group":"weekly_all","percent":12,"resets_at":"2026-11-14T00:00:00Z"},
          {"kind":"weekly","percent":95,"resets_at":"2026-11-20T00:00:00Z","scope":{"model":{"id":"x","name":"X"}}}],
         "extra_usage":{"is_enabled":false,"used_credits":1,"monthly_limit":2}}
        """);
        var fallbackWindows = ClaudeQuotaAdapter.Windows(fallback.RootElement, observed);
        check(fallbackWindows.Length == 2 && (double)fallbackWindows[0]["used_percent"]! == 40 && (double)fallbackWindows[1]["used_percent"]! == 12 && fallbackWindows.All(w => !w.ContainsKey("plan_type")),
            "missing top-level windows use active unscoped limits, preferring the latest reset and then weekly_all");
        foreach (var invalid in new[] { "[]", "{}", """{"five_hour":{"utilization":null,"resets_at":null},"limits":[]}""", """{"extra_usage":{"is_enabled":true}}""" })
        {
            using var malformed = JsonDocument.Parse(invalid);
            var rejected = false;
            try { ClaudeQuotaAdapter.Windows(malformed.RootElement, observed); } catch (JsonException) { rejected = true; }
            check(rejected, "an unreadable Claude usage response cannot overwrite existing evidence");
        }

        foreach (var (profile, expected) in new (string, string?)[] {
            ("""{"account":{"has_claude_max":true,"has_claude_pro":true}}""", "plan_max"), ("""{"account":{"has_claude_max":"false","has_claude_pro":1}}""", "plan_pro"),
            ("""{"account":{"has_claude_max":false},"organization":{"organization_type":"claude_team","subscription_status":"Active"}}""", "plan_team"),
            ("""{"account":{"has_claude_max":false,"has_claude_pro":false}}""", "plan_free"), ("""{"account":{"has_claude_max":false}}""", null), ("[]", null) })
        {
            using var doc = JsonDocument.Parse(profile);
            check(ClaudeQuotaAdapter.ParsePlan(doc.RootElement) == expected, $"Claude profile resolves {expected ?? "no plan"} without guessing");
        }
        check(ClaudeQuotaAdapter.PlanName("plan_max") == "Claude Max" && ClaudeQuotaAdapter.PlanName("max_20x") == "Claude Max 20x" && ClaudeQuotaAdapter.PlanName("") == "" && ClaudeQuotaAdapter.PlanName("enterprise") == "enterprise",
            "Claude plan keys map to readable names and unknown values pass through");

        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(new { items = new[] { new { row_key = "claude", windows } } }));
        var account = QuotaAdapter.Parse(parsed.RootElement, [new AccountSelection("claude", "Claude", "claude", "claude.json", "7", "")], observed)[0];
        check(account.Plan == "Claude Max" && account.Windows.Select(w => w.Label).SequenceEqual(new[] { "5 小时窗口", "周窗口", "周窗口 · Haiku Beta", "周窗口 · claude-opus-4-1", "周窗口 · Opus", "额外用量" }),
            "wallpaper rebuilds Claude labels from window IDs and model scope because CPAMP stores no label");
        check(ClaudeQuotaAdapter.Label("weekly-scoped-fable", []) == "周窗口 · Fable" && ClaudeQuotaAdapter.Label("weekly-scoped-id-claude-opus-4-1", []) == "周窗口 · claude-opus-4-1",
            "name-only Claude model windows regain an initial capital while model IDs stay exact");
        var extraWindow = account.Windows[^1];
        check(extraWindow.Mode == "non_window" && extraWindow.RemainingPercent == 75 && extraWindow.UsedValue == 12.5 && extraWindow.LimitValue == 50 && extraWindow.QuotaUnit == "USD" && !extraWindow.Stale,
            "extra usage reaches the wallpaper as a non-window balance with dollar amounts");

        var root = Path.Combine(Path.GetTempPath(), "wallpaper-claude-" + Guid.NewGuid());
        try
        {
            var store = new PrivateStore(root);
            store.Save(store.Config with { ManagementKey = "test-only", Accounts = [new AccountSelection("claude", "Claude", "claude", "claude.json", "stale-index", "")] });
            var usageCalls = 0; var profileCalls = 0; var writes = 0; var usageMode = "ok"; var profileMode = "ok"; string? writtenPlan = null;
            using var http = new HttpClient(new StubHttpHandler(async (request, token) => {
                if (request.RequestUri!.AbsolutePath.EndsWith("auth-files"))
                    return StubHttpHandler.Json(new { files = new[] { new { name = "claude.json", provider = "claude", auth_index = 7, email = "owner@example.test" } } });
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                if (request.RequestUri.AbsolutePath.EndsWith("api-call"))
                {
                    var value = body.RootElement;
                    var header = value.GetProperty("header");
                    check(value.GetProperty("authIndex").GetString() == "7" && value.GetProperty("method").GetString() == "GET" && header.GetProperty("Authorization").GetString() == "Bearer $TOKEN$"
                        && header.GetProperty("anthropic-beta").GetString() == "oauth-2025-04-20" && !value.TryGetProperty("data", out _), "Claude GETs use the current auth index, CPA token substitution and the OAuth beta header");
                    if (value.GetProperty("url").GetString() == ClaudeQuotaAdapter.ProfileUrl)
                    {
                        profileCalls++;
                        return profileMode == "limited" ? StubHttpHandler.Json(new { status_code = 429, body = "private profile body" })
                            : StubHttpHandler.Json(new { status_code = 200, body = """{"account":{"has_claude_max":true}}""" });
                    }
                    check(value.GetProperty("url").GetString() == ClaudeQuotaAdapter.UsageUrl, "Claude reads only the documented usage and profile endpoints");
                    usageCalls++;
                    return usageMode == "limited" ? StubHttpHandler.Json(new { status_code = 429, body = "private usage body" })
                        : StubHttpHandler.Json(new { status_code = 200, body = usage.RootElement });
                }
                writes++;
                var entry = body.RootElement.GetProperty("entries")[0];
                var written = entry.GetProperty("windows");
                writtenPlan = written[0].TryGetProperty("plan_type", out var plan) ? plan.GetString() : null;
                check(entry.GetProperty("provider").GetString() == "claude" && entry.GetProperty("account").GetProperty("auth_index").GetString() == "7" && written.GetArrayLength() == 6,
                    "Claude writes every readable window against the current identity");
                check(entry.GetProperty("observation").GetProperty("inventory_mode").GetString() == "complete" && entry.GetProperty("observation").GetProperty("inventory_scope_key").GetString() == "claude:quota-windows",
                    "a readable Claude usage response is CPAMP's complete claude:quota-windows inventory");
                return StubHttpHandler.Json(new { items = new[] { new { row_key = "claude", inserted_count = written.GetArrayLength() } } });
            }));
            var clock = observed;
            var refresher = new UpstreamQuotaRefresher(store, http, () => clock);
            check(await refresher.Tick(CancellationToken.None) && refresher.Report().State.Status == "ok" && usageCalls == 1 && profileCalls == 1 && writes == 1 && writtenPlan == "plan_max",
                "automatic Claude refresh reads usage and profile once and writes the plan with the windows");
            check(refresher.Report().State.Accounts![0].Message == "已更新 6 个额度窗口", "Claude refresh reports the written window count");
            clock = clock.AddMinutes(10);
            check(refresher.Queue() && await refresher.Tick(CancellationToken.None) && usageCalls == 2 && profileCalls == 1 && writtenPlan == "plan_max",
                "the Claude plan is cached for an hour instead of querying the profile on every refresh");
            clock = clock.AddHours(2); profileMode = "limited";
            check(refresher.Queue() && await refresher.Tick(CancellationToken.None) && refresher.Report().State.Status == "ok" && profileCalls == 2 && writes == 3 && writtenPlan == "plan_max",
                "a limited profile keeps the recent plan and never fails a readable usage refresh");
            clock = clock.AddMinutes(5);
            check(refresher.Queue() && await refresher.Tick(CancellationToken.None) && profileCalls == 2 && writes == 4, "a failed profile read waits ten minutes before retrying");
            usageMode = "limited"; clock = clock.AddMinutes(10);
            check(refresher.Queue() && await refresher.Tick(CancellationToken.None) && refresher.Report().State.Status == "failed" && usageCalls == 5 && profileCalls == 2 && writes == 4
                && refresher.Report().State.Accounts![0].Message == "上游请求受限，等待下次刷新" && !JsonSerializer.Serialize(refresher.Report()).Contains("private"),
                "a limited Claude usage read writes nothing, skips the profile and hides raw upstream bodies");

            var freshStore = new PrivateStore(Path.Combine(root, "fresh"));
            freshStore.Save(store.Config);
            usageMode = "ok"; profileMode = "limited"; writes = 0;
            var fresh = new UpstreamQuotaRefresher(freshStore, http, () => clock);
            check(await fresh.Tick(CancellationToken.None) && fresh.Report().State.Status == "ok" && writes == 1 && writtenPlan is null,
                "without a readable profile Claude omits plan_type rather than inventing a free plan");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}