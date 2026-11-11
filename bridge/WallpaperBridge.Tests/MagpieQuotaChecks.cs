using System.Net;
using System.Text;
using System.Text.Json;
using WallpaperBridge;

internal static class MagpieQuotaChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var checks = 0;
        void Check(bool value, string label) { check(value, "Magpie: " + label); checks++; }
        foreach (var url in new[] { "http://127.0.0.1:3425", "http://localhost:3425/", "http://[::1]:3425/", "https://quota.example/" })
            Check(MagpieQuotaReader.ValidateUrl(url), "valid URL " + url);
        foreach (var url in new[] { "http://example.com", "http://localhost.example", "https://user:secret@example.com", "https://example.com?q=key", "https://example.com/#secret", "https://example.com/api", "file:///tmp/quota", "" })
            Check(!MagpieQuotaReader.ValidateUrl(url), "unsafe or ambiguous URL rejected");

        var now = DateTimeOffset.Parse("2026-11-10T10:00:00Z");
        const string card = """
        [{"provider":"codex","name":"Codex","kind":"subscription","user":"private@example.com","alias":"工作账号","plan":"plus","readAt":"2026-11-10T09:59:00Z","resets":{"count":3},"windows":[
          {"name":"5h","used":20,"remaining":80,"resetsAt":"2026-11-10T12:00:00Z"},
          {"name":"7d","used":100},
          {"name":"Out of range","remaining":120},
          {"name":"Unknown"},
          {"name":"Unlimited","unlimited":true,"used":0,"remaining":100},
          {"name":"Wallet","balance":15,"used":0,"remaining":100,"unit":"credits"},
          {"name":"Text","text":true,"display":"secret@example.com","remaining":100}
        ]}]
        """;
        QuotaData Parse(string json) { using var doc = JsonDocument.Parse(json); return MagpieQuotaReader.Parse(doc.RootElement, "http://127.0.0.1:3425", now); }
        var data = Parse(card);
        var account = data.Accounts.Single();
        Check(data.Status == "ok" && account.Plan == "GPT Plus" && account.Alias == "工作账号", "display plan and ordinary alias retained");
        Check(account.Id.StartsWith("magpie:") && !account.Id.Contains("private"), "identity is namespaced and hashed");
        Check(account.Windows[0].RemainingPercent == 80 && account.Windows[0].DurationSeconds == 18000 && account.Windows[0].ResetAt == now.AddHours(2), "percentage, reset and duration mapped");
        Check(account.Windows[1].RemainingPercent == 0 && !account.Windows[1].Stale, "exhaustion is preserved as zero");
        Check(account.Windows[2].RemainingPercent == 100, "out of range percentages clamped");
        Check(account.Windows[3].RemainingPercent is null, "missing usage is unknown");
        Check(account.Windows[4].RemainingPercent is null && account.Windows[4].Label.Contains("不限量"), "unlimited is never faked as a percentage");
        Check(account.Windows[5].RemainingPercent is null && account.Windows[6].RemainingPercent is null, "wallet and display-only values are not percentages");
        Check(account.Windows[5].RemainingValue == 15 && account.Windows[4].DisplayText == "不限量", "wallet quantity and unlimited text survive projection");
        var wallet = Parse(card.Replace("\"plan\":\"plus\"", "\"plan\":\"plus\",\"balance\":\"0 credits\""));
        Check(wallet.Accounts[0].Windows.Last().DisplayText == "0 credits" && wallet.Accounts[0].Windows.Last().RemainingPercent is null, "top-level account balance remains a real quantity");
        Check(account.ResetCredits?.Available == 3, "Magpie reset count is mapped");
        Check(account.Windows.All(w => w.ObservedAt == now.AddMinutes(-1)), "observation is the cache read time");
        Check(!JsonSerializer.Serialize(data).Contains("example.com"), "user and vendor display text never enter public DTO");
        Check(Parse(card.Replace("工作账号", "重命名")).Accounts[0].Id == account.Id, "renaming leaves stable identity");
        Check(Parse(card.Replace("09:59:00", "09:00:00")).Status == "stale", "old cache remains stale after a successful fetch");
        Check(Parse(card.Replace("09:59:00", "11:00:00")).Accounts[0].Windows.All(w => w.Stale), "future observations are not current");
        Check(Parse(card.Replace("\"readAt\":\"2026-11-10T09:59:00Z\",", "")).Status == "stale", "missing observation is stale");
        var failure = Parse(card.Replace("\"kind\":\"subscription\"", "\"error\":\"Bearer super-secret@example.com\",\"kind\":\"subscription\""));
        Check(failure.Status == "stale" && !JsonSerializer.Serialize(failure).Contains("super-secret"), "vendor error marks stale without revealing content");
        var unsafeLabels = Parse(card.Replace("工作账号", "private@example.com").Replace("\"plan\":\"plus\"", "\"plan\":\"Bearer secret\""));
        Check(!JsonSerializer.Serialize(unsafeLabels).Contains("Bearer") && unsafeLabels.Accounts[0].Plan == "", "sensitive account labels and plan strings omitted");
        Check(Parse("[]").Status == "empty", "empty cache has no fake account");
        Check(Parse("{\"object\":\"list\",\"data\":" + card + "}").Accounts.Single().Id == account.Id, "actual gateway list envelope is supported");
        // Sanitized shape observed from Magpie's cached HTTP API on 2026-11-11.
        var liveShape = Parse("""
        {"object":"list","data":[{"provider":"codex","name":"Codex","kind":"subscription","user":"fixture-only","plan":"plus","readAt":"2026-11-10T09:59:49Z","windows":[
          {"name":"5 hours","used":40,"remaining":60,"resetsAt":"2026-11-10T12:00:00Z","holdsWhy":""},
          {"name":"7 days","used":8,"remaining":92,"resetsAt":"2026-11-17T09:00:00Z","holdsWhy":""}
        ]}]}
        """).Accounts.Single();
        Check(liveShape.Plan == "GPT Plus" && liveShape.Windows[0].RemainingPercent == 60 && liveShape.Windows[1].RemainingPercent == 92,
            "real gateway window names preserve plan and remaining percentages");
        Check(liveShape.Windows[0].DurationSeconds == 18000 && liveShape.Windows[0].ResetAt == now.AddHours(2) &&
            liveShape.Windows[1].DurationSeconds == 604800 && liveShape.Windows[1].ResetAt == now.AddDays(7).AddHours(-1),
            "real gateway plural window names retain duration and exact reset timestamps");
        Check(Parse(card.Replace("\"name\":\"5h\"", "\"name\":\"24 hours\"")).Accounts[0].Windows[0].DurationSeconds == 86400,
            "plural daily window name retains one-day duration");
        var pools = Parse("""[{"provider":"antigravity","kind":"subscription","windows":[{"name":"5h","pool":"Gemini","remaining":90},{"name":"5h","pool":"Claude & GPT","remaining":20}]}]""").Accounts[0].Windows;
        Check(pools[0].Id != pools[1].Id && pools[0].Label == "Gemini · 5h" && pools[1].Label == "Claude & GPT · 5h", "same-duration provider pools stay separate and identifiable");
        using (var many = JsonDocument.Parse("[" + string.Join(',', Enumerable.Repeat("{}", 257)) + "]"))
        {
            var rejected = false;
            try { MagpieQuotaReader.Parse(many.RootElement, "local", now); } catch (JsonException) { rejected = true; }
            Check(rejected, "account count bounded");
        }

        var calls = 0;
        using (var http = new HttpClient(new FakeHandler(request =>
        {
            calls++;
            Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/magpie/quotas" && request.RequestUri.Query == "?kept=1", "only cached GET endpoint called");
            Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "test-key", "key sent only as authorization header");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"object\":\"list\",\"data\":" + card + "}", Encoding.UTF8, "application/json") };
        })))
        {
            var result = await new MagpieQuotaReader(http).Read("http://127.0.0.1:3425", "test-key", CancellationToken.None);
            Check(calls == 1 && result.Accounts.Length == 1, "reader makes one bounded request");
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Found, HttpStatusCode.InternalServerError })
        {
            using var http = new HttpClient(new FakeHandler(_ => new(status) { Content = new StringContent("secret upstream token") }));
            var result = await new MagpieQuotaReader(http).Read("http://localhost:3425", null, CancellationToken.None);
            Check(result.Status != "ok" && result.Accounts.Length == 0 && !result.Message.Contains("secret"), "HTTP failure sanitized " + status);
        }
        using (var http = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"secret\"}") })))
            Check((await new MagpieQuotaReader(http).Read("http://localhost:3425", null, CancellationToken.None)).Status == "error", "malformed contract is rejected without echo");
        using (var http = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 4 * 1024 * 1024 + 1)) })))
            Check((await new MagpieQuotaReader(http).Read("http://localhost:3425", null, CancellationToken.None)).Status == "error", "oversized payload is rejected");
        Console.WriteLine($"PASS Magpie cached quota reader ({checks} checks)");
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
