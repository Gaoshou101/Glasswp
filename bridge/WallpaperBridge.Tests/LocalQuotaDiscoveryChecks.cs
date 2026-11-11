using WallpaperBridge;
using System.Net;

static class LocalQuotaDiscoveryChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var observed = DateTimeOffset.UtcNow;
        var account = new QuotaAccount("test-local", "本机", "codex", "Plus", []);
        var healthy = new QuotaData("ok", observed, "已读取额度", [account]);
        var absent = new QuotaData("unconfigured", null, "未发现本地登录", []);
        var mixed = LocalQuotaDiscovery.Combine([healthy, absent]);
        check(mixed.Status == "ok" && mixed.Accounts.Length == 1 && mixed.FetchedAt == observed,
            "local discovery ignores absent clients when healthy quotas are available");
        check(LocalQuotaDiscovery.Combine([absent]).Status == "unconfigured"
            && LocalQuotaDiscovery.Combine([]).Status == "unconfigured", "empty local discovery stays unconfigured");
        var denied = new QuotaData("unauthorized", null, "需要重新登录", []);
        var partial = LocalQuotaDiscovery.Combine([healthy, denied]);
        check(partial.Status == "stale" && partial.Accounts.Length == 1 && partial.Message.Contains("需要重新登录"),
            "local discovery retains healthy accounts and discovered login failures");
        check(LocalQuotaDiscovery.Combine([healthy, healthy]).Accounts.Length == 1,
            "local discovery deduplicates repeated native accounts");
        var isolated = await LocalQuotaDiscovery.Group("测试客户端", _ => throw new InvalidOperationException("private-secret"), CancellationToken.None);
        check(isolated.Status == "offline" && !isolated.Message.Contains("private-secret"),
            "local discovery isolates unexpected reader failures without exposing exception contents");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try
        {
            await LocalQuotaDiscovery.Group("测试客户端", _ => throw new OperationCanceledException(cancelled.Token), cancelled.Token);
            check(false, "local discovery propagates caller cancellation");
        }
        catch (OperationCanceledException) { check(true, "local discovery propagates caller cancellation"); }

        var home = Path.Combine(Path.GetTempPath(), "wallpaper-discovery-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(home, ".deepai-code", "userdata", "secrets", "deepai-account.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, "{\"apiKey\":\"fixture-only-deepai\",\"username\":\"fixture-user\"}");
            using var handler = new DeepaiHandler(); using var http = new HttpClient(handler);
            var discovery = new LocalQuotaDiscovery(http, home);
            var first = await discovery.Read(CancellationToken.None);
            var second = await discovery.Read(CancellationToken.None);
            check(first.Status == "ok" && second.Status == "ok" && handler.AllowanceReads == 1,
                "persistent local discovery preserves native daily allowance throttling between refreshes");
            handler.Forbidden = true;
            await File.WriteAllTextAsync(path, "{\"apiKey\":\"changed-fixture-deepai\",\"username\":\"fixture-user\"}");
            await discovery.Read(CancellationToken.None); await discovery.Read(CancellationToken.None);
            check(handler.AllowanceReads == 2,
                "persistent local discovery preserves forbidden allowance cooldown across refreshes");
        }
        finally { Directory.Delete(home, recursive: true); }
    }
    private sealed class DeepaiHandler : HttpMessageHandler
    {
        public int AllowanceReads;
        public bool Forbidden;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.Host != "api.deepai.org") throw new InvalidOperationException("unexpected test endpoint");
            var allowance = request.RequestUri.AbsolutePath.EndsWith("/theos");
            if (allowance) AllowanceReads++;
            return Task.FromResult(new HttpResponseMessage(Forbidden ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                Content = new StringContent(allowance ? "{\"balance\":80,\"granted\":100}" : "{\"plan\":\"Pro\",\"credits_remaining\":10}")
            });
        }
    }
}
