using System.Net;
using System.Text.Json;
using WallpaperBridge;

internal static class AiSourceChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var saved = new AiSourceSettings(Source: "magpie", MagpieUrl: "https://gateway.example.test", MagpieApiKey: "fixture-secret");
        var switched = new AiSourceSettingsInput(Source: "local").Apply(saved);
        check(switched.EffectiveSource == "local" && switched.MagpieUrl == saved.MagpieUrl && switched.MagpieApiKey == saved.MagpieApiKey, "source-only switch retains Magpie address and credential");
        check(new AiSourceSettingsInput().Apply(saved) == saved, "omitted AI source settings preserve saved source and credentials");
        check(new AiSourceSettingsInput(MagpieUrl: "https://example.test").Apply(saved).MagpieApiKey is null, "AI settings never move credential to a different origin");
        check(new AiSourceSettingsInput(ClearMagpieApiKey: true).Apply(saved).MagpieApiKey is null, "AI settings explicitly clear credential");
        foreach (var invalid in new[] { "", "CPAMP", "unknown", "local,magpie" }) {
            var rejected = false; try { new AiSourceSettingsInput(Source: invalid).Apply(saved); } catch (ArgumentException) { rejected = true; }
            check(rejected, "invalid exclusive AI source is rejected: " + invalid);
        }
        var legacy = JsonSerializer.Deserialize<BridgeConfig>(JsonSerializer.Serialize(BridgeConfig.Default, PrivateStore.Json), PrivateStore.Json)!;
        check(legacy.AiSources is null && AiSourceSettings.UsesCpamp(legacy), "legacy configuration defaults to CPAMP without scanning local credentials");
        check(new AiSourceSettings(CodexEnabled: true).EffectiveSource == "local" && new AiSourceSettings(MagpieEnabled: true).EffectiveSource == "magpie", "old independent switches migrate to one effective source");
        var localOptions = new AiSourceOptions(false, 17, [new("local:fixture", "本机别名")]);
        var magpieOptions = new AiSourceOptions(true, 3, [new("magpie:fixture", "网关别名")]);
        var configured = new AiSourceSettingsInput(LocalOptions: localOptions, MagpieOptions: magpieOptions).Apply(saved);
        var switchedOptions = new AiSourceSettingsInput(Source: "local").Apply(configured);
        check(switchedOptions.LocalOptions == localOptions && switchedOptions.MagpieOptions == magpieOptions, "source switch retains independent account selections and schedules");
        check(configured.SameConnection(saved), "account and schedule changes preserve existing connection evidence");
        check(new AiSourceSettingsInput(MagpieUrl: "https://new.example.test", MagpieOptions: magpieOptions).Apply(configured).Options("magpie").Accounts is null, "new Magpie origin clears old account filters even when a stale editor submits them");
        foreach (var invalid in new[] { new AiSourceOptions(RefreshMinutes: 0), new AiSourceOptions(RefreshMinutes: 10081),
            new AiSourceOptions(Accounts: [new("a"), new("a")]), new AiSourceOptions(Accounts: [new("a", new string('x', 61))]),
            new AiSourceOptions(Accounts: [null!]) }) {
            var rejected = false; try { AiSourceOptions.Validate(invalid); } catch (ArgumentException) { rejected = true; }
            check(rejected, "invalid AI source schedule or account settings are rejected");
        }
        var raw = new QuotaData("ok", DateTimeOffset.UtcNow, "fixture", [new("a", "A", "codex", "Plus", []), new("b", "B", "claude", "Pro", [])]);
        check(new AiSourceOptions().Display(raw).Accounts.Length == 2 && new AiSourceOptions(Accounts: []).Display(raw).Accounts.Length == 0, "automatic all and intentional empty selection have distinct meanings");
        var display = new AiSourceOptions(Accounts: [new("b", "我的账号"), new("missing"), new("a")]).Display(raw);
        check(display.Accounts.Select(a => a.Id).SequenceEqual(new[] { "b", "a" }) && display.Accounts[0].Alias == "我的账号" && display.Accounts[1].Alias == "A", "AI account selection applies order and aliases without inventing missing accounts");
        var root = Path.Combine(Path.GetTempPath(), "wallpaper-ai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var store = new PrivateStore(Path.Combine(root, "private"));
            var settings = new AiSourceSettings(Source: "magpie", MagpieApiKey: "fixture-secret");
            store.Save(store.Config with { AiSources = settings, ManagementKey = "cpamp-fixture-key", CpampUrl = "https://cpamp.example.test", Accounts = [new("cpamp-fixture", "CPAMP fixture", "codex", "", "", "")] });
            var hub = new DataHub(store);
            var cpamp = new QuotaAccount("cpamp-fixture", "CPAMP fixture", "codex", "Pro", []);
            hub.Quota(new("ok", DateTimeOffset.UtcNow, "CPAMP fixture", [cpamp]));
            check(hub.Snapshot().Quota.Accounts.Length == 0, "selecting Magpie immediately hides cached CPAMP accounts");
            using var handler = new StubHandler();
            using var source = new AiQuotaSources(store, hub, handler, Path.Combine(root, "anonymous-home"));
            await source.ReadOnce(CancellationToken.None);
            check(handler.Requests == 1 && hub.Snapshot().Quota.Accounts.Length == 1, "selected Magpie is queried independently of CPAMP");
            var magpie = hub.Snapshot().Quota.Accounts.Single();
            store.Save(store.Config with { AiSources = settings with { MagpieOptions = new(Accounts: []) } });
            check(hub.Snapshot().Quota.Accounts.Length == 0 && hub.AiQuotaSnapshot().Accounts.Length == 1, "display selection hides wallpaper accounts while setup discovery remains complete");
            store.Save(store.Config with { AiSources = settings with { MagpieOptions = new(Accounts: [new(magpie.Id, "显示别名")]) } });
            check(hub.Snapshot().Quota.Accounts.Single().Alias == "显示别名" && handler.Requests == 1, "display-only edits apply immediately without another quota read");
            store.Save(store.Config with { AiSources = settings });
            check(magpie.Id != cpamp.Id && hub.CpampQuota().Accounts.Single().Id == cpamp.Id, "Magpie and CPAMP caches never mix their accounts");
            hub.Quota(new("offline", null, "CPAMP unavailable", []));
            check(hub.Snapshot().Quota.Accounts.Single().Id == magpie.Id, "unselected CPAMP result cannot replace active Magpie");
            var local = new AiSourceSettingsInput(Source: "local").Apply(settings);
            store.Save(store.Config with { AiSources = local });
            check(hub.Snapshot().Quota.Accounts.Length == 0, "switching to local immediately removes old Magpie accounts");
            await source.ReadOnce(CancellationToken.None);
            check(handler.Requests == 1 && hub.Snapshot().Quota.Accounts.Length == 0, "empty isolated local source never contacts Magpie or CPAMP");
            hub.AiQuota("magpie", settings, new("ok", DateTimeOffset.UtcNow, "late Magpie response", [magpie]));
            check(hub.Snapshot().Quota.Accounts.Length == 0, "late response from unselected source cannot leak accounts");
            var cpampSettings = new AiSourceSettingsInput(Source: "cpamp").Apply(local);
            store.Save(store.Config with { AiSources = cpampSettings });
            hub.Quota(new("ok", DateTimeOffset.UtcNow, "CPAMP fixture", [cpamp]));
            await source.ReadOnce(CancellationToken.None);
            check(handler.Requests == 1 && hub.Snapshot().Quota.Accounts.Single().Id == cpamp.Id && hub.AiQuotaSnapshot().Accounts.Length == 0, "CPAMP selection excludes local and Magpie data and requests");
            check(store.Config.ManagementKey == "cpamp-fixture-key" && store.Config.Accounts.Single().Id == cpamp.Id && store.Config.CpampUrl == "https://cpamp.example.test" && store.Config.AiSources?.MagpieApiKey == "fixture-secret", "source switching preserves each source's saved configuration");
            var changedOrigin = new AiSourceSettingsInput(Source: "magpie", MagpieUrl: "https://example.test").Apply(settings);
            store.Save(store.Config with { AiSources = changedOrigin });
            check(hub.Snapshot().Quota.Accounts.Length == 0, "changed Magpie origin cannot reuse previous endpoint's accounts");
            hub.AiQuota("magpie", settings, new("ok", DateTimeOffset.UtcNow, "late old origin", [magpie]));
            check(hub.Snapshot().Quota.Accounts.Length == 0, "late old-origin response cannot restore removed source data");
            await source.ReadOnce(CancellationToken.None);
            check(handler.Requests == 2 && handler.Authorizations.Last() is null, "new Magpie origin receives no prior endpoint credential");
            store.Save(store.Config with { AiSources = new(Source: "cpamp") });
            using (var remote = new RemoteSources(store, hub, new StubHttpHandler((_, _) => {
                store.Save(store.Config with { CpampUrl = "https://replacement.example.test" });
                hub.Quota(new("empty", null, "replacement source", []));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }))) {
                await remote.ReadQuotaOnce(CancellationToken.None);
                check(hub.CpampQuota().Accounts.Length == 0 && hub.CpampQuota().Message == "replacement source", "old CPAMP authentication failure cannot restore data after the configured origin changed");
            }
            await RefreshChecks(store, hub, root, check);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static async Task RefreshChecks(PrivateStore store, DataHub hub, string root, Action<bool, string> check)
    {
        var settings = new AiSourceSettings(Source: "magpie"); store.Save(store.Config with { AiSources = settings });
        var now = DateTimeOffset.UtcNow;
        using var handler = new DelayedHandler();
        using var source = new AiQuotaSources(store, hub, handler, Path.Combine(root, "refresh-home"), () => now);
        source.Queue(); source.Queue();
        check(source.RefreshStatus().State.Status == "queued" && source.RefreshStatus().State.LastFinishedAt is null,
            "native quota refresh reports queued before execution and deduplicates repeated clicks");
        await source.StartAsync(CancellationToken.None);
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(source.RefreshStatus().State.Status == "running" && handler.Requests == 1,
                "native quota refresh reports running while the selected provider is in flight");
            handler.Release.TrySetResult();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (source.RefreshStatus().State.Status == "running" && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
            var completed = source.RefreshStatus();
            check(completed.State.Status == "ok" && completed.State.LastFinishedAt == now && completed.State.LastSuccessAt == now
                && hub.Snapshot().Quota.FetchedAt == completed.State.LastFinishedAt,
                "native quota refresh publishes a completed report and snapshot with matching timestamps");
            now = now.AddSeconds(10); source.Queue(); source.Queue();
            var queued = source.RefreshStatus();
            check(queued.State.Status == "queued" && queued.NextRefreshAt == now.AddSeconds(50) && handler.Requests == 1,
                "native manual refresh reports the next allowed read and preserves its sixty-second cooldown");
            now = now.AddSeconds(51);
            deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while ((handler.Requests < 2 || source.RefreshStatus().State.Status is "queued" or "running") && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
            check(handler.Requests == 2 && source.RefreshStatus().State.Status == "ok",
                "queued native refresh runs once when its provider cooldown expires");
            handler.Status = HttpStatusCode.Forbidden;
            now = now.AddSeconds(1); await source.ReadOnce(CancellationToken.None);
            check(source.RefreshStatus().State.Status == "failed" && source.RefreshStatus().State.LastFinishedAt == now
                && source.RefreshStatus().State.LastSuccessAt < now,
                "failed native refresh records completion and preserves the preceding successful read time");
        }
        finally { await source.StopAsync(CancellationToken.None); }

        var disabled = settings with { MagpieOptions = new(false, 9) };
        store.Save(store.Config with { AiSources = disabled });
        using var pausedHandler = new DelayedHandler();
        pausedHandler.Release.TrySetResult();
        using var pausedSource = new AiQuotaSources(store, hub, pausedHandler, Path.Combine(root, "paused-home"), () => now);
        await pausedSource.StartAsync(CancellationToken.None);
        try {
            await Task.Delay(650);
            check(pausedHandler.Requests == 0 && !pausedSource.RefreshStatus().Enabled && pausedSource.RefreshStatus().NextRefreshAt is null, "disabled automatic AI refresh performs no startup request");
            pausedSource.Queue();
            await pausedHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (pausedSource.RefreshStatus().State.Status is "queued" or "running" && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
            check(pausedHandler.Requests == 1 && pausedSource.RefreshStatus().IntervalMinutes == 9, "manual refresh works with automatic refresh disabled and retains configured interval");
            now = now.AddMinutes(20); await Task.Delay(650);
            check(pausedHandler.Requests == 1, "disabled source stays idle after a manual refresh beyond its due time");
        } finally { await pausedSource.StopAsync(CancellationToken.None); }
        store.Save(store.Config with { AiSources = settings });

        using var lateHandler = new DelayedHandler();
        using var lateSource = new AiQuotaSources(store, hub, lateHandler, Path.Combine(root, "late-home"));
        var pending = lateSource.ReadOnce(CancellationToken.None);
        await lateHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.Save(store.Config with { AiSources = new(Source: "local") });
        lateHandler.Release.TrySetResult(); await pending;
        check(lateSource.RefreshStatus().State.Status == "idle" && hub.Snapshot().Quota.Accounts.Length == 0,
            "switching quota source discards the previous reader's late completion and refresh status");
        store.Save(store.Config with { AiSources = settings });
        check(lateSource.RefreshStatus().State.Status == "idle",
            "returning to an interrupted source does not resurrect a permanently running refresh report");
        using var cancelHandler = new DelayedHandler();
        using var cancelSource = new AiQuotaSources(store, hub, cancelHandler, Path.Combine(root, "cancel-home"));
        using var cancellation = new CancellationTokenSource(); var cancelledRead = cancelSource.ReadOnce(cancellation.Token);
        await cancelHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        try { await cancelledRead; check(false, "native refresh cancellation must propagate"); }
        catch (OperationCanceledException)
        {
            check(cancelSource.RefreshStatus().State.Status == "idle",
                "cancelled native refresh propagates cancellation without publishing a failure or frozen running state");
        }
    }
    private sealed class DelayedHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests); Entered.TrySetResult(); await Release.Task.WaitAsync(token);
            return new(Status) { Content = new StringContent("[]") };
        }
    }
    private sealed class StubHandler : HttpMessageHandler
    {
        public int Requests;
        public List<string?> Authorizations = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; Authorizations.Add(request.Headers.Authorization?.Parameter);
            if (request.RequestUri?.PathAndQuery != "/v1/magpie/quotas?kept=1") throw new Exception("unexpected endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { @object = "list", data = new[] {
                new { provider = "codex", name = "fixture", user = "private-identity", kind = "subscription", plan = "plus", readAt = DateTimeOffset.UtcNow,
                    windows = new[] { new { name = "5h", remaining = 75, resetsAt = DateTimeOffset.UtcNow.AddHours(2) } } }
            }})) });
        }
    }
}
