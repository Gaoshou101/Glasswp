using System.Net;
using System.Text;
using System.Text.Json;
using WallpaperBridge;

static class LocalGoogleQuotaChecks
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static string Jwt(object claims) => "head." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";
    public static async Task Run(Action<bool, string> check)
    {
        var home = Path.Combine(Path.GetTempPath(), "wallpaper-google-" + Guid.NewGuid().ToString("N")); var directory = Path.Combine(home, ".gemini"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "oauth_creds.json"); var settings = Path.Combine(directory, "settings.json"); var calls = 0;
        object Login(long expiry, string app = "681255809395-oo8ft2oprdrnp9e3aqf6av3hmdib135j.apps.googleusercontent.com", string access = "private-google-access") => new {
            access_token = access, refresh_token = "never-refresh", expiry_date = expiry, id_token = Jwt(new { sub = "private-google-account", aud = app, email = "private@example.invalid" })
        };
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        var usage = new { buckets = new[] { new { modelId = "gemini-test", tokenType = "REQUESTS", remainingFraction = 0.5, resetTime = "2099-01-01T00:00:00Z" } } };
        using var http = new HttpClient(new Handler((request, _) => {
            calls++; check(request.RequestUri?.Scheme == "https" && request.RequestUri.Host == "cloudcode-pa.googleapis.com" && request.Method == HttpMethod.Post,
                "Gemini local OAuth only targets fixed Code Assist metadata endpoints");
            check(request.Headers.Authorization?.Parameter == "private-google-access", "Gemini metadata uses its own local access token");
            if (request.RequestUri!.AbsolutePath.EndsWith(":loadCodeAssist")) return Task.FromResult(Json(new { paidTier = new { id = "g1-pro-tier" }, cloudaicompanionProject = "private-project" }));
            check(request.RequestUri.AbsolutePath.EndsWith(":retrieveUserQuota"), "Gemini reader never performs onboarding or inference");
            return Task.FromResult(Json(usage));
        }));
        try
        {
            var reader = new LocalGoogleQuotaReader(http, home);
            check((await reader.Read(CancellationToken.None)).Status == "unconfigured" && calls == 0, "absent Google subscriptions create neither invented accounts nor network requests");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Login(future)));
            await File.WriteAllTextAsync(settings, "{\"security\":{\"auth\":{\"selectedType\":\"gemini-api-key\"}}}");
            check((await reader.ReadGemini(CancellationToken.None)).Status == "unconfigured" && calls == 0, "Gemini API key mode excludes obsolete OAuth cache files");
            await File.WriteAllTextAsync(settings, "{\"security\":{\"auth\":{\"selectedType\":\"oauth-personal\"}}}");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Login(future, "another-client")));
            check((await reader.ReadGemini(CancellationToken.None)).Status == "unconfigured" && calls == 0, "another Google application's OAuth token cannot impersonate Gemini CLI");
            var original = JsonSerializer.Serialize(Login(future)); await File.WriteAllTextAsync(path, original);
            var result = await reader.Read(CancellationToken.None);
            check(result.Status == "ok" && result.Accounts.Single().Plan == "Google AI Pro" && result.Accounts.Single().Windows.Single().RemainingPercent == 50 && calls == 2,
                "Gemini CLI local OAuth yields current subscription and model quota while absent AG remains optional");
            var serialized = JsonSerializer.Serialize(result);
            check(!serialized.Contains("private-") && !serialized.Contains("never-refresh") && !serialized.Contains("@"), "Google snapshots expose no token, project, account ID or email");
            check(await File.ReadAllTextAsync(path) == original, "Gemini credential cache is never refreshed or modified");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Login(1)));
            var expired = await reader.ReadGemini(CancellationToken.None);
            check(expired.Status == "unauthorized" && expired.Accounts.Single().Windows.Length == 0 && calls == 2, "expired Gemini OAuth is explicit and never triggers a refresh");
            await File.WriteAllTextAsync(path, original);
            using var switched = new HttpClient(new Handler(async (request, _) => {
                if (request.RequestUri!.AbsolutePath.EndsWith(":loadCodeAssist")) return Json(new { currentTier = new { id = "free-tier" }, cloudaicompanionProject = "project" });
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Login(future, access: "switched"))); return Json(usage);
            }));
            check((await new LocalGoogleQuotaReader(switched, home).ReadGemini(CancellationToken.None)).Accounts.Length == 0, "Gemini login changes discard delayed usage from the previous token");
            using var malformed = JsonDocument.Parse("{\"buckets\":[{\"modelId\":\"unknown\",\"remainingFraction\":null}]}");
            try { LocalGoogleQuotaReader.ParseWindows(malformed.RootElement, DateTimeOffset.UtcNow); check(false, "missing Gemini remaining fraction must fail"); }
            catch (JsonException) { check(true, "missing Gemini balance is unknown rather than zero or full"); }
            using var partial = JsonDocument.Parse("{\"buckets\":[null,\"invalid\",{\"modelId\":\"gemini-pro\",\"remainingFraction\":0.5}]}");
            check(LocalGoogleQuotaReader.ParseWindows(partial.RootElement, DateTimeOffset.UtcNow).Single().RemainingPercent == 50,
                "malformed Gemini bucket cannot hide another measured allowance");
            await File.WriteAllTextAsync(path, original);
            foreach (var label in new[] { "private-google-access plan", "plan private-google-account", "private@example.test", "https://private.example.test", "eyJhbGciOiJub25lIn0.eyJzdWIiOiJhIn0.signature", "Google AI Future 42" })
            {
                using var named = new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith(":loadCodeAssist")
                    ? Json(new { currentTier = new { id = "future-tier", name = label }, cloudaicompanionProject = "private-project" }) : Json(usage))));
                var namedResult = await new LocalGoogleQuotaReader(named, home).ReadGemini(CancellationToken.None);
                check(namedResult.Accounts.Single().Plan == (label == "Google AI Future 42" ? label : ""), "Gemini unknown tier text preserves normal names and strips credentials or identity: " + (label == "Google AI Future 42" ? "normal" : "sensitive"));
            }
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
