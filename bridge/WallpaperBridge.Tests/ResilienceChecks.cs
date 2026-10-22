using System.Text.Json;
using WallpaperBridge;

static class ResilienceChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wallpaper-resilience-" + Guid.NewGuid()));
        try
        {
            var store = new PrivateStore(root);
            var first = new AccountSelection("first", "First", "codex", "first.json", "1", "");
            var second = new AccountSelection("second", "Second", "codex", "second.json", "2", "");
            store.Save(store.Config with { ManagementKey = "test-only", Accounts = [first, second], QuotaRefreshEnabled = false });
            File.WriteAllBytes(Path.Combine(root, "quota-cache.dpapi"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "weather-cache.dpapi"), [1, 2, 3]);
            var hub = new DataHub(store);
            check(hub.Snapshot().Quota.Accounts.Length == 0 && hub.Snapshot().Hardware.Status == "empty" && store.CacheFaults.Length == 2, "corrupt rebuildable caches do not prevent startup or hardware sampling");
            store.Write("quota-cache", new QuotaData("ok", null, "", null!));
            check(new DataHub(store).Snapshot().Quota.Accounts.Length == 0, "valid JSON with invalid quota cache shape also degrades safely");
            var before = hub.Snapshot();
            hub.Quota(Empty.Quota with { Status = "stale" });
            var after = hub.Snapshot();
            check(before.Revisions!.Weather == after.Revisions!.Weather && before.Revisions.Quota != after.Revisions.Quota && before.Revisions.Epoch != new DataHub(store).Snapshot().Revisions!.Epoch, "revisions isolate module changes and identify a new bridge instance");
            var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
            var window = new QuotaWindow("main", "5 小时窗口", 50, null, null, "", observed.AddHours(1), 18000, "fixed", "exact", observed, false);
            hub.Quota(new("ok", observed, "", [new(first.Id, first.Alias, "codex", "", [window]), new(second.Id, second.Alias, "codex", "", [window])]));
            var secondReads = 0;
            using (var remote = new RemoteSources(store, hub, new StubHttpHandler(async (req, token) => {
                if (req.RequestUri!.AbsolutePath.EndsWith("auth-files")) return StubHttpHandler.Json(new { files = new[] { new { name="first.json", provider="codex" }, new { name="second.json", provider="codex" } } });
                using var query = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(token));
                var id = query.RootElement.GetProperty("accounts")[0].GetProperty("row_key").GetString();
                if (id == "first") throw new TaskCanceledException("simulated upstream timeout");
                secondReads++;
                return StubHttpHandler.Json(new { items = new[] { new { row_key = "second", windows = new[] { new { provider_window_id="main", remaining_percent=75, observed_at_ms=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } } } } });
            }))) {
                await remote.ReadQuotaOnce(CancellationToken.None);
                var data = hub.Snapshot().Quota;
                check(secondReads == 1 && data.Accounts[1].Windows[0].RemainingPercent == 75, "first-account timeout cannot skip the next account snapshot");
                check(data.Accounts[0].Windows[0].Stale && data.Accounts[0].Windows[0].ObservedAt == observed, "failed account preserves original evidence timestamp and marks it stale");
            }
            using (var malformed = new RemoteSources(store, hub, new StubHttpHandler((_, _) => Task.FromResult(StubHttpHandler.Json(new { unexpected = true }))))) {
                await malformed.ReadQuotaOnce(CancellationToken.None);
                check(hub.Snapshot().Quota.Status == "stale" && hub.Snapshot().Quota.Accounts.Length == 2, "missing upstream fields degrade to the prior snapshot without throwing");
            }
            Directory.CreateDirectory(Path.Combine(root, "quota-cache.dpapi.tmp"));
            check(!store.WriteCache("quota-cache", hub.Snapshot().Quota), "cache write failure is reported without losing the live snapshot");
            Directory.CreateDirectory(Path.Combine(root, "quota-refresh.dpapi.tmp"));
            using (var http = new HttpClient(new StubHttpHandler((_, _) => throw new Exception("must not query without a checkpoint")))) {
                var refresher = new UpstreamQuotaRefresher(store, http);
                refresher.Queue();
                check(!await refresher.Tick(CancellationToken.None) && refresher.Report().State.Status == "failed", "unwritable refresh checkpoint prevents provider queries and retry storms");
            }
            var quotaRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var weatherPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var remote = new RemoteSources(store, hub, new StubHttpHandler(async (req, token) => {
                if (req.RequestUri!.Host == "api.open-meteo.com") { weatherPending.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                if (req.RequestUri.AbsolutePath.EndsWith("auth-files")) { quotaRead.TrySetResult(); return StubHttpHandler.Json(new { files = Array.Empty<object>() }); }
                return StubHttpHandler.Json(new { items = Array.Empty<object>() });
            }))) {
                await remote.StartAsync(CancellationToken.None);
                await Task.WhenAll(weatherPending.Task, quotaRead.Task).WaitAsync(TimeSpan.FromSeconds(3));
                check(true, "quota starts while weather is still waiting for its response");
                await remote.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token);
                check(remote.ExecuteTask!.IsCompleted, "source workers cancel and finish during shutdown");
            }
            var configPath = Path.Combine(root, "config.dpapi");
            File.WriteAllBytes(configPath, [9,8,7]);
            try { _ = new PrivateStore(root); check(false, "corrupt private config must fail"); }
            catch (System.Security.Cryptography.CryptographicException) { check(File.ReadAllBytes(configPath).SequenceEqual(new byte[]{9,8,7}), "unreadable config is rejected and never overwritten with defaults"); }
        }
        finally
        {
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
