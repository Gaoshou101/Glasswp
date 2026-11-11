using System.Net;
using System.Text;
using System.Text.Json;
using WallpaperBridge;

static class LocalCopilotQuotaChecks
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    public static async Task Run(Action<bool, string> check)
    {
        var home = Path.Combine(Path.GetTempPath(), "wallpaper-copilot-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(home, ".config", "github-copilot"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "apps.json");
        var calls = 0;
        var usage = new { copilot_plan = "individual_pro", quota_reset_date = "2099-01-01", quota_snapshots = new {
            premium_interactions = new { entitlement = "300", quota_remaining = "0", has_quota = false, unlimited = false },
            chat = new { unlimited = true, has_quota = false }
        } };
        using var http = new HttpClient(new Handler((request, _) => {
            calls++; check(request.Method == HttpMethod.Get && request.RequestUri?.Host == "api.github.com" && request.RequestUri.Scheme == "https", "Copilot discovery sends only fixed GitHub HTTPS metadata GETs");
            check(request.Headers.Authorization?.Parameter == "secret-copilot", "Copilot requests use the discovered OAuth token");
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/user" ? Json(new { id = 1234567, login = "private-name", email = "private@example.invalid" }) : Json(usage));
        }));
        try
        {
            check((await new LocalCopilotQuotaReader(http, home).Read(CancellationToken.None)).Status == "unconfigured" && calls == 0, "absent Copilot login creates no account or requests");
            await File.WriteAllTextAsync(path, "{\"github.com.evil\":{\"user\":\"attacker\",\"oauth_token\":\"evil-token\"}}");
            check((await new LocalCopilotQuotaReader(http, home).Read(CancellationToken.None)).Status == "unconfigured" && calls == 0, "file-controlled lookalike GitHub hosts cannot receive credentials");
            var login = "{\"github.com:Iv1.app\":{\"user\":\"private-name\",\"oauth_token\":\"secret-copilot\"}}";
            await File.WriteAllTextAsync(path, login);
            var result = await new LocalCopilotQuotaReader(http, home).Read(CancellationToken.None);
            check(result.Status == "ok" && result.Accounts.Single().Plan == "GitHub Copilot Pro+" && calls == 2, "Copilot editor OAuth login yields authenticated plan and quota");
            var account = result.Accounts.Single();
            check(account.Windows[0].RemainingPercent == 0 && account.Windows[0].UsedValue == 300 && account.Windows[1].RemainingPercent is null && account.Windows[1].Label.Contains("不限量") && account.Windows[1].DisplayText == "不限量", "Copilot exhaustion and unlimited quotas retain distinct semantics and a visible unlimited value");
            var serialized = JsonSerializer.Serialize(result);
            check(!serialized.Contains("1234567") && !serialized.Contains("secret-copilot") && !serialized.Contains("private-name") && !serialized.Contains("@"), "Copilot snapshot hashes authenticated account ID and excludes profile details");
            check(await File.ReadAllTextAsync(path) == login, "Copilot login files are never rewritten");
            Directory.CreateDirectory(Path.Combine(home, ".copilot"));
            await File.WriteAllTextAsync(Path.Combine(home, ".copilot", "config.json"), "{\"lastLoggedInUser\":{\"host\":\"https://github.com\",\"login\":\"private-name\"},\"copilotTokens\":{\"https://github.com:private-name\":\"secret-copilot\"}}");
            check((await new LocalCopilotQuotaReader(http, home).Read(CancellationToken.None)).Accounts.Length == 1 && calls == 4, "same Copilot editor and CLI account is queried once");
            File.Delete(path);
            check((await new LocalCopilotQuotaReader(http, home).Read(CancellationToken.None)).Status == "ok" && calls == 6, "Copilot CLI plaintext token is discovered independently of editor apps.json");
            using var denied = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { error = "secret" }, HttpStatusCode.Unauthorized))));
            var failure = await new LocalCopilotQuotaReader(denied, home).Read(CancellationToken.None);
            check(failure.Status == "unauthorized" && failure.Accounts.Length == 0 && !failure.Message.Contains("secret"), "Copilot denied login does not keep old allowances or leak response bodies");
            using var switched = new HttpClient(new Handler(async (request, _) => {
                if (request.RequestUri!.AbsolutePath == "/user") return Json(new { id = 1234567 });
                await File.WriteAllTextAsync(Path.Combine(home, ".copilot", "config.json"), "{}"); return Json(usage);
            }));
            check((await new LocalCopilotQuotaReader(switched, home).Read(CancellationToken.None)).Accounts.Length == 0, "Copilot account changes during a fetch discard delayed quota");
            using var parsedDocument = JsonDocument.Parse("{\"copilot_plan\":\"enterprise\",\"quota_snapshots\":{\"premium_interactions\":{\"unlimited\":true,\"has_quota\":false}}}");
            check(LocalCopilotQuotaReader.Parse(parsedDocument.RootElement, "id", DateTimeOffset.UtcNow).Windows.Single().RemainingPercent == 0, "Copilot spent enterprise pool is exhausted despite the unlimited flag");
            using var legacyRemaining = JsonDocument.Parse("""{"copilot_plan":"individual","quota_snapshots":{"premium_interactions":{"entitlement":300,"remaining":0}}}""");
            check(LocalCopilotQuotaReader.Parse(legacyRemaining.RootElement, "id", DateTimeOffset.UtcNow).Windows.Single() is { RemainingPercent: 0, UsedValue: 300, QuotaUnit: "requests" }, "Copilot legacy remaining field preserves exhausted requests");
            using var credits = JsonDocument.Parse("""{"copilot_plan":"business","token_based_billing":true,"quota_snapshots":{"premium_interactions":{"entitlement":10000,"quota_remaining":7654.32,"remaining":7654}}}""");
            var creditWindow = LocalCopilotQuotaReader.Parse(credits.RootElement, "id", DateTimeOffset.UtcNow).Windows.Single();
            check(creditWindow.Label == "高级积分" && creditWindow.QuotaUnit == "credits" && Math.Abs(creditWindow.RemainingPercent!.Value - 76.5432) < .0001, "Copilot fractional billing credits keep their units and precise balance");
            using var monthly = JsonDocument.Parse("""{"monthly_quotas":{"chat":50,"completions":2000},"limited_user_quotas":{"chat":30,"completions":0},"limited_user_reset_date":"2099-01-01"}""");
            check(LocalCopilotQuotaReader.Parse(monthly.RootElement, "id", DateTimeOffset.UtcNow).Windows.Select(w => w.RemainingPercent).SequenceEqual(new double?[] { 60, 0 }), "Copilot Free legacy monthly allowance preserves measured chat and completion values");
            using var unlimitedCredits = JsonDocument.Parse("""{"copilot_plan":"enterprise","token_based_billing":true,"quota_snapshots":{"premium_interactions":{"unlimited":true,"has_quota":true,"credits_used":"1234.5"},"completions":{"unlimited":true,"token_based_billing":false}}}""");
            var unlimitedWindows = LocalCopilotQuotaReader.Parse(unlimitedCredits.RootElement, "id", DateTimeOffset.UtcNow).Windows;
            check(unlimitedWindows[0] is { RemainingPercent: null, LimitValue: null, UsedValue: 1234.5, QuotaUnit: "credits", DisplayText: "不限量" } && unlimitedWindows[1].QuotaUnit == "requests", "Copilot unlimited credits show measured spend without inventing a seat budget or changing completion units");
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
