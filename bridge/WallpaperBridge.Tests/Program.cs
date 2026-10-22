using System.Text.Json;
using System.Text.Json.Nodes;
using WallpaperBridge;
var checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
var desktopPoint = new DesktopInputPoint(800, 1280, 1600, 2560);
Check(DesktopInputGuard.Matches(desktopPoint, true, -400, 640, -800, 0, 800, 1280), "desktop gesture matches mixed DPI and negative monitor origins");
Check(!DesktopInputGuard.Matches(desktopPoint, false, -400, 640, -800, 0, 800, 1280), "covered application gesture cannot authorize wallpaper input");
Check(!DesktopInputGuard.Matches(desktopPoint, true, -300, 640, -800, 0, 800, 1280), "desktop release at another coordinate cannot authorize wallpaper input");
Check(!DesktopInputGuard.Matches(desktopPoint, true, 960, 540, 0, 0, 1920, 1080), "release forwarded from a landscape display cannot activate portrait wallpaper");
Check(!DesktopInputGuard.Matches(desktopPoint with { X = double.NaN }, true, -400, 640, -800, 0, 800, 1280), "invalid viewport coordinates fail closed");
Check(DesktopWindowEvents.Matches(30, -176, -29, -176, -29), "minimize release remains blocked after the app window disappears");
Check(!DesktopWindowEvents.Matches(801, -176, -29, -176, -29), "minimize protection expires without disabling later desktop clicks");
Check(!DesktopWindowEvents.Matches(30, -800, 1095, -176, -29), "minimizing an app does not block a different desktop card");
var windowEvents = new DesktopWindowEvents();
await windowEvents.StartAsync(CancellationToken.None);
Check(windowEvents.Available, "native minimize event subscription is available");
await windowEvents.StopAsync(CancellationToken.None);
Check(!windowEvents.Available, "native minimize event subscription detaches on shutdown");
var desktopGuard = new DesktopInputGuard(windowEvents);
Check(desktopGuard.Available, "native hit test is available without a global input hook");
Check(!desktopGuard.Allows(desktopPoint with { X = double.NaN }), "native hit test rejects invalid input");
var selected = new[] { new AccountSelection("row", "Codex", "codex", "local.json", "1", "") };
var now = DateTimeOffset.FromUnixTimeMilliseconds(100000000);
using var doc = JsonDocument.Parse("""
{"items":[{"row_key":"row","provider":"codex","windows":[
{"provider_window_id":"a","used_percent":100,"duration_seconds":18000,"cycle_end_ms":100001000,"observed_at_ms":100000000,"window_mode":"fixed","boundary_accuracy":"exact"},
{"provider_window_id":"b","remaining_percent":null,"observed_at_ms":1,"window_mode":"rolling","boundary_accuracy":"estimated"},
{"provider_window_id":"c","remaining_percent":130,"observed_at_ms":100000000,"field_sources":{"remaining_percent":{"observed_at_ms":1}}},
{"provider_window_id":"d","remaining_percent":10,"availability":"inactive"}]}]}
""");
var result = QuotaAdapter.Parse(doc.RootElement, selected, now);
Check(result.Length == 1 && result[0].Windows.Length == 3, "inactive windows excluded");
Check(result[0].Windows[0].RemainingPercent == 0, "used percentage conversion preserves exhausted quota");
Check(result[0].Windows[1].RemainingPercent is null && result[0].Windows[1].Stale, "missing and old evidence are not zero");
Check(result[0].Windows[1].Mode == "rolling" && result[0].Windows[1].Accuracy == "estimated", "provider semantics retained");
Check(result[0].Windows[2].RemainingPercent == 100 && result[0].Windows[2].Stale, "clamp and balance field freshness");
Check(result[0].Windows[0].ResetAt?.ToUnixTimeMilliseconds() == 100001000, "reset timestamp normalization");
using var imageQuota = JsonDocument.Parse("""
{"items":[{"row_key":"row","windows":[
{"provider_window_id":"window-1d-0","window_kind":"daily","model_scope_kind":"family","model_scope_key":"codex_main","duration_seconds":86400,"used_percent":25,"observed_at_ms":100000000},
{"provider_window_id":"imagegen-premium-0-window-1d-0","window_kind":"daily","model_scope_kind":"feature","model_scope_key":"imagegen_premium","duration_seconds":86400,"used_percent":0,"observed_at_ms":100000000}]}]}
""");
var imageAccount = QuotaAdapter.Parse(imageQuota.RootElement, selected, now)[0];
Check(imageAccount.Windows.Length == 2 && imageAccount.Windows[0].Label == "日窗口" && imageAccount.Windows[1].Label == "日窗口 · 绘图（Premium）", "real image quota retained with explicit daily feature label");
Check(imageAccount.Windows[1].RemainingPercent == 100, "image zero usage is full remaining balance");
using var imageCached = JsonDocument.Parse("""
{"quota":{"observed_at":"2026-10-10T00:00:00Z","signals":{"X-Codex-Active-Limit":"imagegen_premium","X-Codex-Primary-Used-Percent":"0","X-Codex-Primary-Window-Minutes":"1440","X-Codex-Primary-Reset-After-Seconds":"120"}}}
""");
var imageHeader = CachedHeaderAdapter.ParseCodex(imageCached.RootElement, selected[0], DateTimeOffset.Parse("2026-10-10T00:00:30Z"));
Check(imageHeader.Windows.Single().Label == "日窗口 · 绘图（Premium）", "cached image quota uses same feature label");
Check(imageHeader.Windows.Single().Id == "cached-header-imagegen-premium-primary", "cached image quota identity is separate from coding headers");
Check(CachedHeaderAdapter.Merge(imageAccount with { Windows = [imageAccount.Windows[0]] }, imageHeader).Windows.Length == 2, "cached image quota cannot merge into a coding daily budget");
Check(CachedHeaderAdapter.Merge(imageAccount, imageHeader).Windows.Length == 2, "cached image quota does not duplicate canonical image budget");
using var legacyImage = JsonDocument.Parse("""
{"items":[{"row_key":"row","windows":[{"provider_window_id":"window-1d-0","window_kind":"daily","model_scope_kind":"family","model_scope_key":"codex_main","source":"response_header","source_observation_id":"image-request","duration_seconds":86400,"used_percent":1,"remaining_percent":99,"cycle_end_ms":186400000,"observed_at_ms":100000000}]}]}
""");
using var imageEvent = JsonDocument.Parse("""
{"events":{"has_more":false,"items":[{"event_hash":"image-hash","request_id":"image-request","timestamp_ms":100000000,"auth_file_snapshot":"local.json","auth_provider_snapshot":"codex","model":"gpt-image-2.5-sunburst","response_metadata":{"quota":{"active_limit":"imagegen_premium","primary":{"window_minutes":1440,"used_percent":1}}}}]}}
""");
var scopeReads = 0;
var localClassifier = new LocalQuotaClassifier();
var correctedImage = await localClassifier.Classify(legacyImage.RootElement, selected[0], now, (body, _) => {
    using var request = JsonDocument.Parse(JsonSerializer.Serialize(body));
    Check(request.RootElement.GetProperty("from_ms").GetInt64() == 100000000 && request.RootElement.GetProperty("to_ms").GetInt64() == 100000001 && request.RootElement.GetProperty("filters").GetProperty("auth_files")[0].GetString() == "local.json", "image evidence lookup restricted to exact observation and account");
    scopeReads++; return Task.FromResult(imageEvent.RootElement);
}, CancellationToken.None);
var correctedAccount = QuotaAdapter.Parse(correctedImage, selected, now)[0];
Check(correctedAccount.Windows.Single().Label == "日窗口 · 绘图（Premium）", "stock CPAMP misclassified daily quota corrected locally using exact request evidence");
Check(correctedAccount.Windows.Single().RemainingPercent == 99 && correctedAccount.Windows.Single().ResetAt?.ToUnixTimeMilliseconds() == 186400000, "local classification preserves CPAMP selected balance and boundary");
Check(legacyImage.RootElement.GetProperty("items")[0].GetProperty("windows")[0].GetProperty("model_scope_key").GetString() == "codex_main", "local classification leaves source snapshot unchanged");
await localClassifier.Classify(legacyImage.RootElement, selected[0], now.AddSeconds(30), (_, _) => throw new Exception("cached provenance read again"), CancellationToken.None);
Check(scopeReads == 1, "unchanged provenance uses a bounded local cache");
async Task<QuotaAccount> ClassifyImageCase(Action<JsonNode, JsonNode> mutate)
{
    var input = JsonNode.Parse(legacyImage.RootElement.GetRawText())!;
    var evidence = JsonNode.Parse(imageEvent.RootElement.GetRawText())!;
    mutate(input["items"]![0]!["windows"]![0]!, evidence);
    var changed = await new LocalQuotaClassifier().Classify(JsonSerializer.SerializeToElement(input), selected[0], now, (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(evidence)), CancellationToken.None);
    return QuotaAdapter.Parse(changed, selected, now)[0];
}
foreach (var (field, value) in new[] { ("auth_file_snapshot", "another.json"), ("auth_provider_snapshot", "claude"), ("request_id", "another-request") })
    Check((await ClassifyImageCase((_, e) => e["events"]!["items"]![0]![field] = value)).Windows.Single().Label == "日窗口", "mismatched " + field + " cannot reclassify quota");
Check((await ClassifyImageCase((_, e) => e["events"]!["items"]![0]!["timestamp_ms"] = 100000001)).Windows.Single().Label == "日窗口", "newer image observation cannot reclassify old daily quota");
Check((await ClassifyImageCase((_, e) => e["events"]!["items"]![0]!["response_metadata"]!["quota"]!["primary"]!["window_minutes"] = 300)).Windows.Single().Label == "日窗口", "different window duration cannot reclassify quota");
Check((await ClassifyImageCase((_, e) => e["events"]!["items"]![0]!["response_metadata"]!["quota"]!["primary"]!["used_percent"] = 2)).Windows.Single().Label == "日窗口", "different observed balance cannot reclassify quota");
Check((await ClassifyImageCase((_, e) => e["events"]!["items"]![0]!["response_metadata"]!["quota"]!["active_limit"] = "premium")).Windows.Single().Label == "日窗口", "explicit coding active limit overrides image model name");
Check((await ClassifyImageCase((_, e) => e["events"]!["items"]![0]!["response_metadata"]!["quota"]!.AsObject().Remove("active_limit"))).Windows.Single().Label == "日窗口 · 绘图", "image model fallback works only when active limit is absent");
Check((await ClassifyImageCase((_, e) => { e["events"]!["items"]![0]!["response_metadata"]!["quota"]!.AsObject().Remove("active_limit"); e["events"]!["items"]![0]!["resolved_model"] = "gpt-codex"; })).Windows.Single().Label == "日窗口", "resolved coding model prevents misleading image model fallback");
Check((await ClassifyImageCase((_, e) => e["events"]!["has_more"] = true)).Windows.Single().Label == "日窗口", "truncated event evidence is not sufficient to reclassify");
Check((await ClassifyImageCase((_, e) => e["events"]!["items"]!.AsArray().Add(e["events"]!["items"]![0]!.DeepClone()))).Windows.Single().Label == "日窗口", "ambiguous event provenance fails closed");
Check((await ClassifyImageCase((w, _) => w["source"] = "api_query")).Windows.Single().Label == "日窗口", "genuine API daily quota remains unchanged");
Check((await ClassifyImageCase((w, _) => w["field_sources"] = JsonNode.Parse("""{"quota":{"source":"api_query","observed_at_ms":100000000}}"""))).Windows.Single().Label == "日窗口", "balance field provenance takes precedence over header boundary source");
Check((await ClassifyImageCase((w, _) => { w["observed_at_ms"] = 100000001; w["source_observation_id"] = "boundary-request"; w["field_sources"] = JsonNode.Parse("""{"quota":{"source":"response_header","observed_at_ms":100000000}}"""); })).Windows.Single().Label == "日窗口 · 绘图（Premium）", "balance event is matched independently of a newer boundary observation");
var unavailableImage = await new LocalQuotaClassifier().Classify(legacyImage.RootElement, selected[0], now, (_, _) => throw new HttpRequestException("unsupported endpoint"), CancellationToken.None);
Check(unavailableImage.GetRawText() == legacyImage.RootElement.GetRawText(), "unavailable analytics degrades to original quota without breaking snapshot reads");
using (var cancelledImage = new CancellationTokenSource()) {
    cancelledImage.Cancel();
    try { await new LocalQuotaClassifier().Classify(legacyImage.RootElement, selected[0], now, (_, _) => throw new OperationCanceledException(cancelledImage.Token), cancelledImage.Token); }
    catch (OperationCanceledException) { checks++; }
}
Check(CachedHeaderAdapter.Merge(correctedAccount, imageHeader).Windows.Length == 1, "corrected snapshot and raw image headers do not create a second image page");
var duplicateImage = JsonNode.Parse(legacyImage.RootElement.GetRawText())!;
var canonicalImage = JsonNode.Parse(correctedImage.GetRawText())!["items"]![0]!["windows"]![0]!.DeepClone();
canonicalImage["observed_at_ms"] = 99999999;
canonicalImage["used_percent"] = 0;
canonicalImage["remaining_percent"] = 100;
duplicateImage["items"]![0]!["windows"]!.AsArray().Add(canonicalImage);
var deduplicatedImage = await new LocalQuotaClassifier().Classify(JsonSerializer.SerializeToElement(duplicateImage), selected[0], now, (_, _) => Task.FromResult(imageEvent.RootElement), CancellationToken.None);
var deduplicatedAccount = QuotaAdapter.Parse(deduplicatedImage, selected, now)[0];
Check(deduplicatedAccount.Windows.Length == 1 && deduplicatedAccount.Windows[0].RemainingPercent == 99, "legacy and canonical image scopes collapse to the newest complete observation");
if (args.Length == 2 && args[0] == "--image-fixture")
{
    using var liveImageFixture = JsonDocument.Parse(File.ReadAllText(args[1]));
    foreach (var fixture in liveImageFixture.RootElement.GetProperty("cases").EnumerateArray())
    {
        var target = JsonSerializer.Deserialize<AccountSelection>(fixture.GetProperty("selection"), PrivateStore.Json)!;
        var classified = await new LocalQuotaClassifier().Classify(fixture.GetProperty("snapshot"), target, DateTimeOffset.FromUnixTimeMilliseconds(fixture.GetProperty("nowMs").GetInt64()), (_, _) => Task.FromResult(fixture.GetProperty("events")), CancellationToken.None);
        var parsed = QuotaAdapter.Parse(classified, [target], now)[0];
        Check(parsed.Windows.Single().Label == "日窗口 · 绘图（Premium）", "captured original CPA image quota replays with correct local feature label");
    }
}
using var cached = JsonDocument.Parse("""
{"name":"local.json","auth_index":7,"account":"member@example.test","email":"MEMBER@example.test","id_token":{"chatgpt_account_id":"workspace"},"quota":{"observed_at":"2026-10-10T00:00:00Z","signals":{"X-Codex-Plan-Type":"team","X-Codex-Primary-Used-Percent":"15","X-Codex-Primary-Window-Minutes":"300","X-Codex-Primary-Reset-After-Seconds":"120","X-Codex-Secondary-Used-Percent":"0","X-Codex-Secondary-Window-Minutes":"0"}}}
""");
var identity = CachedHeaderAdapter.Identity(cached.RootElement, selected[0]);
Check(identity.AccountId == "workspace" && identity.AccountSnapshot == "member@example.test" && identity.AuthIndex == "7", "Codex member and workspace identity normalized");
var header = CachedHeaderAdapter.ParseCodex(cached.RootElement, selected[0], DateTimeOffset.Parse("2026-10-10T01:00:00Z"));
Check(header.Windows.Length == 1 && header.Windows[0].RemainingPercent == 85, "cached response header balance; zero-duration window excluded");
Check(header.Windows[0].ResetAt == DateTimeOffset.Parse("2026-10-10T00:02:00Z") && header.Windows[0].Accuracy == "derived" && header.Windows[0].Stale, "relative reset uses observation time, not fetch time");
var scoped = header with { Windows = header.Windows.Select(w => w with { Label = "5 小时窗口 · model_scope" }).ToArray() };
Check(CachedHeaderAdapter.Merge(scoped, header).Windows.Length == 1, "generic header does not invent duplicate model-scope quota");
var canonicalAt = DateTimeOffset.Parse("2026-10-10T00:00:00Z");
var canonical = new QuotaAccount("row", "Codex", "codex", "team", [
    header.Windows[0] with { Id = "five-hour", RemainingPercent = 42, ResetAt = canonicalAt.AddHours(1), Accuracy = "exact", ObservedAt = canonicalAt, Stale = false }
]);
var newerHeader = header with { Windows = [header.Windows[0] with { RemainingPercent = 85, ResetAt = canonicalAt.AddHours(5), ObservedAt = canonicalAt.AddMinutes(1), Stale = false }] };
var canonicalMerged = CachedHeaderAdapter.Merge(canonical, newerHeader);
Check(canonicalMerged.Windows[0] == canonical.Windows[0], "newer response headers cannot replace CPAMP's selected balance and reset boundary");
var oldBalanceNewBoundary = canonical with { Windows = [canonical.Windows[0] with { ObservedAt = canonicalAt.AddHours(-1), Stale = true }] };
Check(CachedHeaderAdapter.Merge(oldBalanceNewBoundary, newerHeader).Windows[0] == oldBalanceNewBoundary.Windows[0], "old balance observation does not authorize overwriting a CPAMP boundary");
Check(CachedHeaderAdapter.Merge(canonical with { Windows = [] }, newerHeader).Windows[0] == newerHeader.Windows[0], "response headers still supply a missing quota window");
Check(CachedHeaderAdapter.Merge(canonical, newerHeader, snapshotAvailable: false).Windows[0] == newerHeader.Windows[0], "failed snapshot query may fall back to a newer cached header");
Check(CachedHeaderAdapter.Merge(canonical, header with { Windows = [header.Windows[0] with { ObservedAt = canonicalAt.AddMinutes(-1) }] }, snapshotAvailable: false).Windows[0] == canonical.Windows[0], "failed snapshot query retains previous evidence when headers are older");
using var conflict = JsonDocument.Parse("""{"account":"a@example.test","email":"b@example.test"}""");
Check(CachedHeaderAdapter.Identity(conflict.RootElement, selected[0]).AccountSnapshot == "", "conflicting member identity fails closed");
try { PrivateStore.Validate(BridgeConfig.Default with { CpampUrl = "http://example.com" }); throw new Exception("insecure address accepted"); } catch (ArgumentException) { checks++; }
var temp = Path.Combine(Path.GetTempPath(), "wallpaper-test-" + Guid.NewGuid());
var store = new PrivateStore(temp);
var testKey = "test-only-not-a-real-key";
store.Save(BridgeConfig.Default with { ManagementKey = testKey });
Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(temp, "config.dpapi"))).Contains(testKey), "DPAPI file contains no plaintext key");
var restored = new PrivateStore(temp);
Check(restored.Config.ManagementKey == testKey && restored.ReadToken == store.ReadToken, "encrypted config and pairing survive restart");
var secondSelection = selected[0] with { Id = "second", Alias = "Second" };
var missingSelection = selected[0] with { Id = "missing", Alias = "Missing" };
store.Save(store.Config with { Accounts = [secondSelection, selected[0] with { Alias = "Renamed" }, missingSelection] });
store.Write("quota-cache", new QuotaData("ok", now, "cached", [result[0], result[0] with { Id = "second" }]));
var cachedHub = new DataHub(new PrivateStore(temp));
Check(cachedHub.Snapshot().Quota.Accounts.Select(a => a.Id).SequenceEqual(new[] { "second", "row", "missing" }), "saved display order applies to cached quota immediately after restart");
Check(cachedHub.Snapshot().Quota.Accounts[1].Alias == "Renamed" && cachedHub.Snapshot().Quota.Accounts[2].Windows.Length == 0, "cached order retains current aliases and selected accounts without evidence");
foreach (var file in Directory.EnumerateFiles(temp)) File.Delete(file); Directory.Delete(temp);

var legacy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(BridgeConfig.Default, PrivateStore.Json))!.AsObject();
legacy.Remove("quotaRefreshEnabled"); legacy.Remove("quotaRefreshMinutes");
var legacyConfig = legacy.Deserialize<BridgeConfig>(PrivateStore.Json)!;
Check(legacyConfig.QuotaRefreshEnabled && legacyConfig.QuotaRefreshMinutes == 10, "old config defaults to upstream refresh every ten minutes");
var configured = BridgeConfig.Default with { ManagementKey = testKey, Accounts = selected };
var attempt = new QuotaRefreshState(LastAttemptAt: now, Status: "failed");
Check(QuotaRefreshSchedule.Next(configured, attempt, now) == now.AddMinutes(10) && !QuotaRefreshSchedule.Due(configured, attempt, now.AddMinutes(9)), "failed attempts retain interval and prevent restart retry storms");
Check(QuotaRefreshSchedule.Next(configured with { QuotaRefreshEnabled = false }, attempt, now) is null && QuotaRefreshSchedule.Next(BridgeConfig.Default, attempt, now) is null, "disabled or unconfigured sources do not schedule");
Check(QuotaRefreshSchedule.Next(configured with { QuotaRefreshMinutes = 37 }, attempt, now) == now.AddMinutes(37), "custom interval changes next due time");
foreach (var minutes in new[] { 0, 10081 })
    try { PrivateStore.Validate(configured with { QuotaRefreshMinutes = minutes }); throw new Exception("invalid interval accepted"); } catch (ArgumentException) { checks++; }

using var weeklyUsage = JsonDocument.Parse("""{"plan_type":"team","rate_limit":{"primary_window":{"used_percent":0,"limit_window_seconds":604800,"reset_at":200000},"secondary_window":{"used_percent":0,"limit_window_seconds":0}}}""");
var weekly = UpstreamQuotaAdapter.Windows(weeklyUsage.RootElement, now);
using var currentScope = JsonDocument.Parse("""{"items":[{"row_key":"row","windows":[{"provider_window_id":"old-model","duration_seconds":604800,"model_scope_key":"base_model_inference","observed_at_ms":1},{"provider_window_id":"five-hour","duration_seconds":18000,"model_scope_key":"codex_main","observed_at_ms":100000000},{"provider_window_id":"weekly","duration_seconds":604800,"model_scope_key":"codex_main","observed_at_ms":100000000}]}]}""");
var cardOrder = CachedHeaderAdapter.Merge(QuotaAdapter.Parse(currentScope.RootElement, selected, now)[0], new("row", "Codex", "codex", "", []));
Check(cardOrder.Windows.Select(w => w.Id).SequenceEqual(new[] { "five-hour", "weekly", "old-model" }) && cardOrder.Windows[1].Label == "周窗口", "main upstream windows lead cards and implementation scope names stay out of labels");
Check(weekly.Length == 1 && (string)weekly[0]["window_kind"]! == "weekly" && (double)weekly[0]["remaining_percent"]! == 100, "primary may be weekly; true zero usage preserved; absent duration excluded");
Check((string)weekly[0]["boundary_accuracy"]! == "exact" && (long)weekly[0]["cycle_end_ms"]! == 200000000, "upstream absolute reset takes priority");
using var mixedUsage = JsonDocument.Parse("""
{"rateLimit":{"primaryWindow":{"limitWindowSeconds":"18000","resetAfterSeconds":"90"},"secondaryWindow":{"usedPercent":"100","limitWindowSeconds":604800}},"code_review_rate_limit":{"primary_window":{"used_percent":9,"limit_window_seconds":604800}},"additional_rate_limits":[{"metered_feature":"codex_spark","rate_limit":{"primary_window":{"used_percent":5,"limit_window_seconds":18000}}}]}
""");
var mixed = UpstreamQuotaAdapter.Windows(mixedUsage.RootElement, now);
Check(mixed.Length == 4 && !mixed[0].ContainsKey("remaining_percent") && (double)mixed[1]["remaining_percent"]! == 0, "missing upstream usage remains unknown and exhausted usage remains zero");
Check((long)mixed[0]["cycle_end_ms"]! == now.AddSeconds(90).ToUnixTimeMilliseconds() && (string)mixed[0]["boundary_accuracy"]! == "derived", "relative upstream reset uses observation time");
Check((string)mixed[0]["model_scope_key"]! == "codex_main" && (string)mixed[2]["model_scope_key"]! == "code_review" && (string)mixed[3]["model_scope_kind"]! == "models" && (string)mixed[0]["container_provider_window_id"]! == "weekly", "main, code review and Spark scopes stay distinct with concurrent cadence relation");
using var writeShape = JsonDocument.Parse(JsonSerializer.Serialize(UpstreamQuotaAdapter.SnapshotWrite(selected[0], mixed)));
Check(!writeShape.RootElement.GetProperty("entries")[0].TryGetProperty("observation", out _), "snapshot write preserves unobserved scopes through CPAMP partial inventory");

var refreshTemp = Path.Combine(Path.GetTempPath(), "wallpaper-refresh-test-" + Guid.NewGuid());
try
{
    var refreshStore = new PrivateStore(refreshTemp);
    var refreshSelections = new[] { new AccountSelection("row", "Healthy", "codex", "healthy.json", "old", ""), new AccountSelection("bad", "Limited", "codex", "limited.json", "old", ""), new AccountSelection("third", "After failure", "codex", "third.json", "old", ""), new AccountSelection("other", "Other provider", "claude", "other.json", "old", "") };
    refreshStore.Save(configured with { Accounts = refreshSelections });
    var clock = now;
    var calls = new List<string>();
    var handler = new StubHttpHandler(async (req, token) =>
    {
        calls.Add(req.RequestUri!.AbsolutePath);
        Check(req.Headers.Authorization?.Parameter == testKey, "upstream operations retain management authentication inside bridge");
        if (req.RequestUri.AbsolutePath.EndsWith("auth-files"))
            return StubHttpHandler.Json(new { files = refreshSelections.Where(a => a.Provider == "codex").Select((a, i) => new { name = a.AuthFile, provider = a.Provider, auth_index = i + 1, email = "member@example.test", id_token = new { chatgpt_account_id = "workspace" } }) });
        using var requestBody = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(token));
        if (req.RequestUri.AbsolutePath.EndsWith("api-call"))
        {
            var body = requestBody.RootElement;
            Check(body.GetProperty("method").GetString() == "GET" && body.GetProperty("url").GetString() == "https://chatgpt.com/backend-api/wham/usage" && body.GetProperty("header").GetProperty("Authorization").GetString() == "Bearer $TOKEN$", "fixed upstream usage GET delegates OAuth substitution to CPA");
            var index = body.GetProperty("authIndex").GetString();
            if (index == "2") return StubHttpHandler.Json(new { status_code = 429, body = "private error must not escape" });
            return index == "1" ? StubHttpHandler.Json(new { status_code = 200, body = weeklyUsage.RootElement.GetRawText() }) : StubHttpHandler.Json(new { status_code = 200, body = weeklyUsage.RootElement });
        }
        var entry = requestBody.RootElement.GetProperty("entries")[0];
        Check(entry.GetProperty("account").GetProperty("auth_index").GetString() is "1" or "3", "current discovered auth_index used for observation write");
        return StubHttpHandler.Json(new { items = new[] { new { row_key = entry.GetProperty("row_key").GetString(), inserted_count = entry.GetProperty("windows").GetArrayLength() } } });
    });
    using var http = new HttpClient(handler);
    var refresher = new UpstreamQuotaRefresher(refreshStore, http, () => clock);
    Check(await refresher.Tick(CancellationToken.None), "configured first run fetches upstream immediately");
    var report = refresher.Report();
    Check(report.State.Status == "partial" && report.State.Accounts!.Select(a => a.Status).SequenceEqual(new[] { "ok", "failed", "ok", "unsupported" }), "wrapper status checked and each account failure isolated");
    Check(calls.Count(p => p.EndsWith("api-call")) == 3 && calls.Count(p => p.EndsWith("quota-snapshots")) == 2 && !JsonSerializer.Serialize(report).Contains("private error"), "only successful observations written and raw failure bodies suppressed");
    Check(!await refresher.Tick(CancellationToken.None), "local polling cannot repeatedly query upstream");
    var resumed = new UpstreamQuotaRefresher(new PrivateStore(refreshTemp), http, () => clock);
    Check(!await resumed.Tick(CancellationToken.None) && resumed.Report().NextRefreshAt == now.AddMinutes(10), "refresh attempt and next time survive encrypted persistence and restart");
    refreshStore.Save(refreshStore.Config with { City = BridgeConfig.Default.City with { Name = "Other city" }, Accounts = refreshSelections.Select(a => a with { Alias = a.Alias + " renamed" }).ToArray() });
    Check(!await refresher.Tick(CancellationToken.None), "city and display alias edits do not trigger upstream refresh");
    refreshStore.Save(refreshStore.Config with { Accounts = refreshStore.Config.Accounts.Reverse().ToArray() });
    Check(!await refresher.Tick(CancellationToken.None) && refresher.Report().State.LastFinishedAt == report.State.LastFinishedAt, "display order changes preserve upstream schedule and last refresh result");
    refreshStore.Save(refreshStore.Config with { QuotaRefreshEnabled = false, QuotaRefreshMinutes = 2 });
    clock = now.AddMinutes(11);
    Check(!await refresher.Tick(CancellationToken.None) && refresher.Report().NextRefreshAt is null, "disabled automatic refresh sends no requests when overdue");
    Check(refresher.Queue() && !refresher.Queue(), "manual requests are deduplicated");
    Check(await refresher.Tick(CancellationToken.None), "manual upstream refresh works with automatic refresh disabled");
    refreshStore.Save(refreshStore.Config with { QuotaRefreshEnabled = true });
    clock = now.AddMinutes(13);
    Check(await refresher.Tick(CancellationToken.None), "custom two-minute interval actually becomes due");

    // Pause in-flight request while configuration changes; no old observation may be written.
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var wrote = false;
    using var changingHttp = new HttpClient(new StubHttpHandler(async (req, token) =>
    {
        if (req.RequestUri!.AbsolutePath.EndsWith("auth-files")) return StubHttpHandler.Json(new { files = new[] { new { name = "healthy.json", provider = "codex", auth_index = "1", email = "member@example.test", id_token = new { chatgpt_account_id = "workspace" } } } });
        if (req.RequestUri.AbsolutePath.EndsWith("api-call")) { entered.SetResult(); await release.Task; return StubHttpHandler.Json(new { status_code = 200, body = weeklyUsage.RootElement }); }
        wrote = true; return StubHttpHandler.Json(new { items = Array.Empty<object>() });
    }));
    refreshStore.Save(configured with { Accounts = [refreshSelections[0]] });
    var changing = new UpstreamQuotaRefresher(refreshStore, changingHttp, () => clock);
    var tick = changing.Tick(CancellationToken.None);
    await entered.Task;
    Check(!await changing.Tick(CancellationToken.None) && !changing.Queue(), "concurrent trigger cannot duplicate running refresh");
    refreshStore.Save(refreshStore.Config with { CpampUrl = "https://new-source.example.test" });
    release.SetResult(); await tick;
    Check(!wrote && changing.Report().State.Status == "idle", "changing source discards old in-flight result before snapshot write");
}
finally { foreach (var file in Directory.EnumerateFiles(refreshTemp)) File.Delete(file); Directory.Delete(refreshTemp); }
if (args.Length == 2 && args[0] == "--reset-fixture")
{
    using var liveFixture = JsonDocument.Parse(File.ReadAllText(args[1]));
    var capturedAt = DateTimeOffset.Parse(liveFixture.RootElement.GetProperty("capturedAt").GetString()!);
    foreach (var fixture in liveFixture.RootElement.GetProperty("cases").EnumerateArray())
    {
        var selection = fixture.GetProperty("selected").Deserialize<AccountSelection>(PrivateStore.Json)!;
        var snapshot = QuotaAdapter.Parse(fixture.GetProperty("snapshot"), [selection], capturedAt)[0];
        var cachedHeaders = CachedHeaderAdapter.ParseCodex(fixture.GetProperty("header"), selection, capturedAt);
        var merged = CachedHeaderAdapter.Merge(snapshot, cachedHeaders);
        foreach (var window in snapshot.Windows)
            Check(merged.Windows.Single(w => w.Id == window.Id) == window, "captured CPAMP window retains its balance and reset: " + selection.Id + "/" + window.Id);
    }
}
var weatherNow = DateTimeOffset.Parse("2026-10-13T15:30:00Z");
var weatherCity = new City("test", "测试城市", 31, 120, "Asia/Shanghai");
using var weatherDocument = JsonDocument.Parse(JsonSerializer.Serialize(new {
    current = new { time = weatherNow.ToUnixTimeSeconds(), temperature_2m = 22, weather_code = 2, is_day = 0 },
    daily = new { temperature_2m_min = new double?[] { 18, 16 }, temperature_2m_max = new double?[] { 25, 24 } },
    hourly = new { time = new double?[] { weatherNow.AddHours(-2).ToUnixTimeSeconds(), weatherNow.AddMinutes(-30).ToUnixTimeSeconds(), weatherNow.AddMinutes(30).ToUnixTimeSeconds(), weatherNow.AddHours(25).ToUnixTimeSeconds(), 253402300799 },
        temperature_2m = new double?[] { 20, null, 21, 19 }, precipitation_probability = new double?[] { 0, 0, 150 }, precipitation = new double?[] { 0, 0, 1.2 }, weather_code = new int[] { 2, 2, 61 } }
}));
var weatherResult = WeatherAdapter.Parse(weatherDocument.RootElement, weatherCity, weatherNow);
Check(weatherResult.Hourly?.Length == 2, "hourly weather excludes elapsed and out-of-range slots without failing current conditions");
Check(weatherResult.Hourly![0].Temperature is null && weatherResult.Hourly[0].PrecipitationProbability == 0 && weatherResult.Hourly[0].PrecipitationMm == 0, "hourly weather preserves missing temperature and real zero rain");
Check(weatherResult.Hourly[1].PrecipitationProbability == 100 && weatherResult.Hourly[1].PrecipitationMm == 1.2, "hourly weather clamps probability but preserves rain amount");
Check(weatherResult.Hourly[1].Time == DateTimeOffset.Parse("2026-10-13T16:00:00Z") && weatherResult.Timezone == "Asia/Shanghai", "forecast epoch preserves next-day local hour and city timezone");
Check(weatherResult.Low == 18 && weatherResult.High == 25, "two-day forecast retains today's daily range");
using var noHourlyWeather = JsonDocument.Parse("""{"current":{"temperature_2m":23,"weather_code":2,"is_day":1},"daily":{"temperature_2m_min":[19],"temperature_2m_max":[26]}}""");
Check(WeatherAdapter.Parse(noHourlyWeather.RootElement, weatherCity, weatherNow).Hourly?.Length == 0, "missing hourly forecast does not discard current weather");
var legacyWeather = JsonSerializer.Deserialize<WeatherData>("""{"status":"stale","city":"测试城市","temperature":23,"low":19,"high":26,"isDay":true,"message":"旧缓存"}""", PrivateStore.Json)!;
Check(legacyWeather.Hourly is null && legacyWeather.Temperature == 23, "old weather cache remains compatible with optional forecast fields");

// Request activity is credential scoped and only projects display-safe fields.
var activityNow = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
var dayBounds = new ActivityQuery("today", "Asia/Shanghai").Bounds(activityNow);
Check(dayBounds.From == DateTimeOffset.Parse("2026-10-13T16:00:00Z") && dayBounds.To == activityNow, "today starts at the same local midnight as CPAMP");
var yesterdayBounds = new ActivityQuery("yesterday", "Asia/Shanghai").Bounds(activityNow);
Check(yesterdayBounds.From == DateTimeOffset.Parse("2026-10-12T16:00:00Z") && yesterdayBounds.To == dayBounds.From, "yesterday excludes today and uses the prior local day");
Check(new ActivityQuery("7d", "Asia/Shanghai").Bounds(activityNow).From == DateTimeOffset.Parse("2026-10-07T16:00:00Z"), "default seven days includes today and six previous days rather than 168 hours");
Check(new ActivityQuery("14d", "Asia/Shanghai").Bounds(activityNow).From == dayBounds.From.AddDays(-13) && new ActivityQuery("30d", "Asia/Shanghai").Bounds(activityNow).From == dayBounds.From.AddDays(-29), "fourteen and thirty days match CPAMP range boundaries");
Check(new ActivityQuery("all", "UTC").Bounds(activityNow).From.ToUnixTimeMilliseconds() == 1, "all history uses CPAMP's supported start timestamp");
var dstBounds = new ActivityQuery("yesterday", "America/New_York").Bounds(DateTimeOffset.Parse("2026-03-09T14:00:00Z"));
Check((dstBounds.To - dstBounds.From).TotalHours == 23, "yesterday handles a daylight-saving local day");
var customQuery = new ActivityQuery("custom", "Asia/Shanghai", activityNow.AddHours(-2).ToUnixTimeMilliseconds(), activityNow.AddHours(-1).ToUnixTimeMilliseconds());
Check(customQuery.Bounds(activityNow).From == activityNow.AddHours(-2) && customQuery.Bounds(activityNow).To == activityNow.AddHours(-1), "custom range preserves exact chosen timestamps");
foreach (var invalidQuery in new[] { new ActivityQuery("unknown"), new ActivityQuery("custom"), customQuery with { ToMs = customQuery.FromMs }, new ActivityQuery(TimeZone: "invalid/time-zone"), new ActivityQuery(BeforeMs: activityNow.ToUnixTimeMilliseconds(), BeforeId: 1) }) {
    var rejected = false; try { invalidQuery.Bounds(activityNow); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "invalid range, timezone or cursor rejected before upstream query");
}
using var activityDoc = JsonDocument.Parse("""
{"summary":{"total_calls":2,"success_rate":0.5,"total_tokens":0,"total_cost":0},"events":{"has_more":false,"items":[
{"timestamp_ms":1791975600000,"auth_file_snapshot":"local.json","auth_provider_snapshot":"codex","model":"gpt-codex","failed":false,"total_tokens":0,"input_tokens":0,"output_tokens":0,"cache_read_tokens":0,"latency_ms":0,"ttft_ms":0,"api_key_hash":"SECRET-SENTINEL","session_id":"PRIVATE-SENTINEL"},
{"timestamp_ms":1791972000000,"auth_file_snapshot":"local.json","auth_provider_snapshot":"codex","model":"gpt-codex","failed":true,"fail_status_code":429,"total_tokens":null,"latency_ms":null,"fail_summary":"SECRET-SENTINEL"}]}}
""");
var activity = AccountActivityReader.Parse(activityDoc.RootElement, selected[0], activityNow.AddDays(-7), activityNow);
Check(activity.Requests == 2 && activity.SuccessRate == 50 && activity.EstimatedCost == 0 && activity.Tokens == 0, "activity preserves counts, ratio and actual zero cost/tokens");
Check(activity.Recent.Length == 2 && activity.Recent[0].Success == true && activity.Recent[0].DurationMs == 0 && activity.Recent[0].TtftMs == 0 && activity.LastActivity == activity.Recent[0].Time, "activity sorts requests and preserves zero duration and TTFT");
Check(activity.Recent[1].Success == false && activity.Recent[1].StatusCode == 429 && activity.Recent[1].Tokens is null && activity.Recent[1].DurationMs is null && activity.Recent[1].TtftMs is null, "activity failure and unknown fields stay distinct");
using var independentTtftDoc = JsonDocument.Parse(activityDoc.RootElement.GetRawText().Replace("\"latency_ms\":0", "\"latency_ms\":12500").Replace("\"ttft_ms\":0", "\"ttft_ms\":750"));
var independentLatency = AccountActivityReader.Parse(independentTtftDoc.RootElement, selected[0], activityNow.AddDays(-7), activityNow).Recent[0];
Check(independentLatency.TtftMs == 750 && independentLatency.DurationMs == 12500, "TTFT and total duration preserve independent upstream measurements");
using var invalidTtftDoc = JsonDocument.Parse(independentTtftDoc.RootElement.GetRawText().Replace("\"ttft_ms\":750", "\"ttft_ms\":-1"));
var missingLatency = AccountActivityReader.Parse(invalidTtftDoc.RootElement, selected[0], activityNow.AddDays(-7), activityNow).Recent[0];
Check(missingLatency.TtftMs is null && missingLatency.DurationMs == 12500, "invalid TTFT stays unknown without estimating from total duration");
Check(JsonSerializer.Deserialize<RequestActivity>("""{"time":"2026-10-14T11:00:00Z","model":"gpt-codex","durationMs":12500}""", PrivateStore.Json)?.TtftMs is null, "legacy activity without TTFT stays compatible");
var activityJson = JsonSerializer.Serialize(activity, PrivateStore.Json);
Check(!activityJson.Contains("SENTINEL") && !activityJson.Contains("local.json") && !activityJson.Contains("auth_index"), "activity projection excludes credentials, session identifiers and arbitrary error bodies");
bool mismatchRejected = false;
try { AccountActivityReader.Parse(activityDoc.RootElement, selected[0] with { AuthFile = "other.json" }, activityNow.AddDays(-7), activityNow); } catch (JsonException) { mismatchRejected = true; }
Check(mismatchRejected, "activity rejects a different credential's rows");
using var activityRequest = JsonDocument.Parse(JsonSerializer.Serialize(AccountActivityReader.BuildRequest(selected[0], activityNow.AddDays(-7), activityNow)));
Check(activityRequest.RootElement.GetProperty("filters").GetProperty("auth_files")[0].GetString() == "local.json" && activityRequest.RootElement.GetProperty("filters").GetProperty("include_failed").GetBoolean(), "activity query fixes credential scope and includes failures");
Check(activityRequest.RootElement.GetProperty("include").GetProperty("events_page").GetProperty("limit").GetInt32() == 20, "activity rows are bounded independently from the seven-day summary");
using var cursorRequest = JsonDocument.Parse(JsonSerializer.Serialize(AccountActivityReader.BuildRequest(selected[0], customQuery.Bounds(activityNow).From, customQuery.Bounds(activityNow).To, customQuery with { BeforeMs = activityNow.AddMinutes(-90).ToUnixTimeMilliseconds(), BeforeId = 42 })));
Check(cursorRequest.RootElement.GetProperty("include").GetProperty("events_page").GetProperty("before_id").GetInt64() == 42 && cursorRequest.RootElement.GetProperty("time_zone").GetString() == "Asia/Shanghai", "CPAMP cursor and timezone forwarded alongside fixed credential scope");
using var cursorDoc = JsonDocument.Parse(activityDoc.RootElement.GetRawText().Replace("\"has_more\":false", "\"has_more\":true,\"total_count\":2,\"next_before_ms\":1791972000000,\"next_before_id\":42"));
var cursorActivity = AccountActivityReader.Parse(cursorDoc.RootElement, selected[0], activityNow.AddDays(-7), activityNow);
Check(cursorActivity.NextBeforeMs == 1791972000000 && cursorActivity.NextBeforeId == 42 && cursorActivity.TotalCount == 2, "pagination metadata preserves CPAMP timestamp-plus-ID cursor");
var activityTemp = Path.Combine(Path.GetTempPath(), "wallpaper-activity-test-" + Guid.NewGuid());
try {
    var activityStore = new PrivateStore(activityTemp);
    activityStore.Save(BridgeConfig.Default with { ManagementKey = "test-only", Accounts = selected });
    var activityReads = 0; var activityFails = false;
    using var activityReader = new AccountActivityReader(activityStore, new StubHttpHandler((request, token) => {
        activityReads++;
        if (activityFails) throw new HttpRequestException("test-only offline");
        return Task.FromResult(StubHttpHandler.Json(cursorDoc.RootElement));
    }));
    var allActivity = await activityReader.Read("row", CancellationToken.None, new ActivityQuery("all", "UTC"));
    var cachedActivity = await activityReader.Read("row", CancellationToken.None, new ActivityQuery("all", "UTC"));
    Check(activityReads == 1 && ReferenceEquals(allActivity, cachedActivity), "repeat range uses its own cached activity");
    await activityReader.Read("row", CancellationToken.None, new ActivityQuery("7d", "UTC"));
    Check(activityReads == 2, "another range cannot reuse all-history summary cache");
    var pageQuery = new ActivityQuery("all", "UTC", allActivity!.From.ToUnixTimeMilliseconds(), allActivity.To.ToUnixTimeMilliseconds(), allActivity.NextBeforeMs, allActivity.NextBeforeId);
    var olderActivity = await activityReader.Read("row", CancellationToken.None, pageQuery);
    Check(activityReads == 3 && olderActivity?.From == allActivity.From && olderActivity.To == allActivity.To && olderActivity.LastActivity == allActivity.LastActivity, "page has separate cache and preserves frozen range and latest activity");
    activityFails = true;
    var failedRange = await activityReader.Read("row", CancellationToken.None, new ActivityQuery("14d", "UTC"));
    Check(failedRange?.Status == "offline" && failedRange.Requests is null && failedRange.Recent.Length == 0, "failed range never substitutes another range's cached statistics");
    activityFails = false;
    activityStore.Save(activityStore.Config with { Accounts = [selected[0] with { Alias = "Updated" }] });
    await activityReader.Read("row", CancellationToken.None, new ActivityQuery("all", "UTC"));
    Check(activityReads == 5, "config revision invalidates all activity range and cursor caches");
} finally { foreach (var file in Directory.EnumerateFiles(activityTemp)) File.Delete(file); Directory.Delete(activityTemp); }
using var resetDoc = JsonDocument.Parse("""
{"items":[{"row_key":"row","windows":[{"provider_window_id":"main","observed_at_ms":1791979200000,"reset_credits_available":0,"field_sources":{"reset_credits_available":{"observed_at_ms":1791979200000}}}]}]}
""");
var resets = QuotaAdapter.Parse(resetDoc.RootElement, selected, activityNow)[0].ResetCredits;
Check(resets?.Available == 0 && !resets.Stale, "zero available reset credits remain valid evidence");
Check(QuotaAdapter.Parse(resetDoc.RootElement, selected, activityNow.AddMinutes(16))[0].ResetCredits?.Stale == true, "reset credit freshness uses its own evidence time");
Check(QuotaAdapter.Parse(resetDoc.RootElement, [selected[0] with { Provider = "claude" }], activityNow)[0].ResetCredits is null, "Codex reset credits are not shown for other providers");
using var resetUsage = JsonDocument.Parse("""{"rate_limit_reset_credits":{"available_count":3},"rate_limit":{"primary_window":{"limit_window_seconds":18000,"used_percent":20}}}""");
Check((int)UpstreamQuotaAdapter.Windows(resetUsage.RootElement, activityNow)[0]["reset_credits_available"]! == 3, "normal Codex usage refresh writes available reset credit evidence");
using var legacyAccountDoc = JsonDocument.Parse("""{"id":"a","alias":"A","provider":"codex","plan":"Pro","windows":[]}""");
Check(JsonSerializer.Deserialize<QuotaAccount>(legacyAccountDoc.RootElement, PrivateStore.Json)?.ResetCredits is null, "legacy quota cache without reset credit fields stays compatible");

Console.WriteLine($"{checks} bridge checks passed");

sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    public static HttpResponseMessage Json(object data) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), System.Text.Encoding.UTF8, "application/json") };
}
