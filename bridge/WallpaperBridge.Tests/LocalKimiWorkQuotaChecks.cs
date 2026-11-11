using System.Net;
using System.Text.Json;
using WallpaperBridge;

internal static class LocalKimiWorkQuotaChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        JsonElement Data(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
        var parsed = LocalKimiWorkQuotaReader.Parse(Data("""{"subscriptionBalance":{"amountUsedRatio":0.3,"kimiCodeUsedRatio":0.1}}"""), now);
        check(parsed.Single().RemainingPercent == 70 && parsed[0].DisplayText == "Code 已用 10%（占总额）", "Kimi Work Code share stays in same monthly pool");
        check(LocalKimiWorkQuotaReader.Parse(Data("""{"subscription_balance":{}}"""), now)[0].RemainingPercent == 100, "Kimi Work Connect omitted zero is unused within existing balance");
        check(LocalKimiWorkQuotaReader.Parse(Data("{}"), now).Length == 0 && LocalKimiWorkQuotaReader.Parse(Data("""{"subscriptionBalance":{"amountUsedRatio":"bad"}}"""), now).Length == 0, "Kimi Work missing or malformed balance has no fake allowance");
        check(LocalKimiWorkQuotaReader.Parse(Data("""{"subscriptionBalance":{"amountUsedRatio":null}}"""), now).Length == 0
            && LocalKimiWorkQuotaReader.Parse(Data("""{"subscriptionBalance":{"amountUsedRatio":"bad","amount_used_ratio":0}}"""), now).Length == 0,
            "Kimi Work explicit null or conflicting malformed ratios never become full allowance");
        var home = Path.Combine(Path.GetTempPath(), "wallpaper-kimi-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
        try
        {
            var userData = Path.Combine(home, "AppData", "Roaming", "kimi-desktop"); Directory.CreateDirectory(userData);
            var share = Path.Combine(home, "moved", "daimon-share"); Directory.CreateDirectory(Path.Combine(share, "daimon"));
            await File.WriteAllTextAsync(Path.Combine(userData, "daimon-storage.json"), JsonSerializer.Serialize(new { shareDir = share }));
            var login = """{"credentials":{"kimiCode":{"apiKey":"private-work-key","baseUrl":"https://agent-gw.kimi.com/coding/v1"},"kimiWeb":{"accessToken":"private-web-token","refreshToken":"unused-refresh","userId":"private-uid"}}}""";
            var path = Path.Combine(share, "daimon", "config.json"); await File.WriteAllTextAsync(path, login);
            var calls = 0;
            using var client = new HttpClient(new Handler(async request =>
            {
                calls++; check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/GetSubscriptionStats") && await request.Content!.ReadAsStringAsync() == "{}", "Kimi Work only calls read-only membership RPC");
                check(request.Headers.Authorization?.Parameter == "private-web-token" && request.Headers.GetValues("x-msh-platform").Single() == "windows", "Kimi Work uses desktop web identity and headers");
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"subscriptionBalance\":{\"amountUsedRatio\":0.3}}") };
            }));
            var data = await new LocalKimiWorkQuotaReader(client, home).Read(CancellationToken.None);
            check(data.Status == "ok" && data.Accounts.Single().Windows[0].RemainingPercent == 70 && calls == 1, "Kimi Work discovers relocated desktop config");
            check(await File.ReadAllTextAsync(path) == login && !JsonSerializer.Serialize(data).Contains("private"), "Kimi Work never writes credentials or exports private identity");
            using (var switched = new HttpClient(new Handler(_ =>
            {
                File.WriteAllText(path, login.Replace("private-web-token", "changed-web-token"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"subscriptionBalance\":{}}") });
            })))
                check((await new LocalKimiWorkQuotaReader(switched, home).Read(CancellationToken.None)).Accounts.Length == 0, "Kimi Work discards delayed membership after desktop session changes");
            await File.WriteAllTextAsync(path, login);
            using var unauthorized = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-body") })));
            var failure = await new LocalKimiWorkQuotaReader(unauthorized, home).Read(CancellationToken.None);
            check(failure.Status == "unauthorized" && !failure.Message.Contains("secret-body"), "Kimi Work rejected membership never refreshes token");
            await File.WriteAllTextAsync(path, login.Replace("https://agent-gw.kimi.com/coding/v1", "https://malicious.example"));
            check((await new LocalKimiWorkQuotaReader(client, home).Read(CancellationToken.None)).Status == "unconfigured" && calls == 1, "Kimi Work custom endpoint config is not borrowed");
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request); }
}
