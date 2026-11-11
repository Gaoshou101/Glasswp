using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WallpaperBridge;

internal static class LocalKiroQuotaChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        using var doc = JsonDocument.Parse("""
        {"subscriptionInfo":{"subscriptionTitle":"KIRO PRO"},"nextDateReset":1900000000,"overageConfiguration":{"overageStatus":"ENABLED"},"usageBreakdownList":[
        {"currentUsageWithPrecision":125,"usageLimitWithPrecision":100,"overageCapWithPrecision":50,"freeTrialInfo":{"freeTrialStatus":"ACTIVE","currentUsageWithPrecision":10,"usageLimitWithPrecision":20},"bonuses":[{"status":"ACTIVE","currentUsage":0,"usageLimit":30,"expiresAt":1900000000},{"status":"EXPIRED","currentUsage":0,"usageLimit":10}]}]}
        """);
        var parsed = LocalKiroQuotaReader.ParseUsage(doc.RootElement, now, "us-east-1");
        check(parsed.Plan == "KIRO PRO" && parsed.Windows.Length == 4, "Kiro maps plan, trial, monthly, overage and live bonuses");
        check(parsed.Windows.Single(w => w.Id.EndsWith(":plan")).RemainingPercent == 0, "Kiro plan exhaustion remains zero");
        check(parsed.Windows.Single(w => w.Id.EndsWith(":overage")).RemainingPercent == 50, "Kiro overage uses independent cap");
        using var unknown = JsonDocument.Parse("""{"usageBreakdownList":[{"usageLimitWithPrecision":100}]}""");
        check(LocalKiroQuotaReader.ParseUsage(unknown.RootElement, now, "eu-central-1").Windows[0].RemainingPercent is null, "Kiro unknown consumption does not become unused");
        var home = Path.Combine(Path.GetTempPath(), "wallpaper-kiro-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var ideDir = Path.Combine(home, ".aws", "sso", "cache"); Directory.CreateDirectory(ideDir);
            var authFile = Path.Combine(ideDir, "kiro-auth-token.json");
            var auth = """{"accessToken":"fixture-private-token","provider":"BuilderId","expiresAt":"2099-01-01T00:00:00Z"}""";
            await File.WriteAllTextAsync(authFile, auth);
            var calls = 0;
            using var client = new HttpClient(new Handler(request =>
            {
                calls++;
                check(request.Method == HttpMethod.Get && request.RequestUri!.Host == "management.us-east-1.kiro.dev" && request.RequestUri.AbsolutePath == "/Get-Usage-Limits", "Kiro local Builder login only performs quota GET");
                check(request.Headers.Authorization?.Parameter == "fixture-private-token", "Kiro uses local access token in auth header");
                return new(HttpStatusCode.OK) { Content = new StringContent(doc.RootElement.GetRawText()) };
            }));
            var data = await new LocalKiroQuotaReader(client, home).Read(CancellationToken.None);
            check(data.Status == "ok" && data.Accounts.Length == 1 && calls == 1, "Kiro discovers IDE credentials and reads allowance");
            check(await File.ReadAllTextAsync(authFile) == auth && !JsonSerializer.Serialize(data).Contains("fixture-private-token"), "Kiro leaves credentials unchanged and out of public DTO");
            using (var switched = new HttpClient(new Handler(_ =>
            {
                File.WriteAllText(authFile, auth.Replace("fixture-private-token", "replacement-token"));
                return new(HttpStatusCode.OK) { Content = new StringContent(doc.RootElement.GetRawText()) };
            })))
                check((await new LocalKiroQuotaReader(switched, home).Read(CancellationToken.None)).Accounts.Length == 0, "Kiro discards a delayed result after IDE login changes");
            await File.WriteAllTextAsync(authFile, auth.Replace("2099-01-01", "2000-01-01"));
            check((await new LocalKiroQuotaReader(client, home).Read(CancellationToken.None)).Status == "unauthorized" && calls == 1, "Kiro expired local credential is not refreshed");
            File.Delete(authFile);
            var dbDir = Path.Combine(home, "AppData", "Roaming", "kiro-cli"); Directory.CreateDirectory(dbDir);
            using (var db = new SqliteConnection("Data Source=" + Path.Combine(dbDir, "data.sqlite3") + ";Pooling=false"))
            {
                await db.OpenAsync(); using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE auth_kv (key TEXT, value TEXT); INSERT INTO auth_kv VALUES ($key, $value)";
                command.Parameters.AddWithValue("$key", "kirocli:odic:token");
                command.Parameters.AddWithValue("$value", """{"access_token":"fixture-private-token","expires_at":"2099-01-01T00:00:00Z"}""");
                await command.ExecuteNonQueryAsync();
            }
            var cli = await new LocalKiroQuotaReader(client, home).Read(CancellationToken.None);
            check(cli.Status == "ok" && cli.Accounts.Length == 1 && calls == 2, "Kiro discovers CLI read-only SQLite login");
            await File.WriteAllTextAsync(authFile, "{malformed");
            File.Delete(Path.Combine(dbDir, "data.sqlite3"));
            check((await new LocalKiroQuotaReader(client, home).Read(CancellationToken.None)).Status == "offline", "Kiro corrupted native login is a failure, not absent installation");
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request)); }
}
