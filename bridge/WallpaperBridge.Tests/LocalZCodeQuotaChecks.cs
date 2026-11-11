using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WallpaperBridge;

internal static class LocalZCodeQuotaChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        using var limits = JsonDocument.Parse("""{"level":"pro","limits":[{"unit":3,"number":5,"type":"TOKENS_LIMIT","usage":100,"remaining":0},{"unit":6,"number":1,"percentage":25},{"usage":0,"percentage":100},{"unit":3,"usage":200}]}""");
        var windows = LocalZCodeQuotaReader.ParseLimits(limits.RootElement, now);
        check(windows[0].RemainingPercent == 0 && windows[0].DurationSeconds == 18000, "ZCode zero remaining and five-hour span preserved");
        check(windows[1].RemainingPercent == 75 && windows[1].DurationSeconds == 604800, "ZCode percentage-only weekly quota mapped");
        check(windows[2].RemainingPercent is null && windows[2].Label.Contains("不限量") && windows[3].RemainingPercent is null, "ZCode unlimited and unknown usage never invent percentage");
        using var gifts = JsonDocument.Parse("""{"server_time":1794300000,"plans":[{"status":"active","plan_id":"gift","name":"Start Plan","starts_at":1,"ends_at":1900000000}],"balances":[{"plan_id":"gift","show_name":"GLM-5","total_units":"100","remaining_units":"75","expires_at":1900000000},{"plan_id":"missing","total_units":100,"used_units":5}]}""");
        var gift = LocalZCodeQuotaReader.ParseBalance(gifts.RootElement, now);
        check(gift.Windows.Length == 1 && gift.Windows[0].RemainingPercent == 75 && gift.Plan == "Start Plan", "ZCode active gifts map string counts and skip unknown plans");
        var home = Path.Combine(Path.GetTempPath(), "wallpaper-zcode-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var settings = Path.Combine(home, ".zcode", "v2"); Directory.CreateDirectory(settings);
            var moved = Path.Combine(home, "relocated"); var dataDir = Path.Combine(moved, ".zcode", "v2"); Directory.CreateDirectory(dataDir);
            await File.WriteAllTextAsync(Path.Combine(settings, "setting.json"), JsonSerializer.Serialize(new { dataBaseDir = moved }));
            var cipher = SHA256.HashData(Encoding.UTF8.GetBytes("zcode-credential-fallback:win32:" + home + ":" + Environment.UserName.Split('\\').Last()));
            var encrypted = Encrypt(cipher, "fixture.secret");
            check(LocalZCodeQuotaReader.Decrypt(cipher, encrypted) == "fixture.secret" && LocalZCodeQuotaReader.Decrypt(new byte[32], encrypted) == "", "ZCode AES-GCM authentication rejects wrong key");
            var credentials = JsonSerializer.Serialize(new Dictionary<string, string> { ["account-provider:coding-plan:account:zai-individual-coding-plan:account:fixture:api-key"] = encrypted, ["oauth:zai:user_info"] = Encrypt(cipher, "{\"id\":\"private-user\",\"email\":\"private@example.com\"}") });
            var file = Path.Combine(dataDir, "credentials.json"); await File.WriteAllTextAsync(file, credentials);
            var calls = 0;
            using var client = new HttpClient(new Handler(request =>
            {
                calls++; check(request.Method == HttpMethod.Get && request.RequestUri!.Host == "api.z.ai" && request.RequestUri.AbsolutePath == "/api/monitor/usage/quota/limit", "ZCode local login only performs quota GET");
                check(request.Headers.GetValues("Authorization").Single() == "fixture.secret", "ZCode coding key goes in authorization header");
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":200,\"data\":" + limits.RootElement.GetRawText() + "}") };
            }));
            var result = await new LocalZCodeQuotaReader(client, home).Read(CancellationToken.None);
            check(result.Status == "ok" && result.Accounts.Single().Plan == "GLM Coding pro" && calls == 1, "ZCode discovers moved encrypted login and coding allowance");
            check(await File.ReadAllTextAsync(file) == credentials && !JsonSerializer.Serialize(result).Contains("private") && !JsonSerializer.Serialize(result).Contains("fixture.secret"), "ZCode login stays unchanged and identity is hashed");
            using (var switched = new HttpClient(new Handler(_ =>
            {
                File.WriteAllText(file, credentials.Replace(encrypted, Encrypt(cipher, "replacement.secret")));
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":200,\"data\":" + limits.RootElement.GetRawText() + "}") };
            })))
                check((await new LocalZCodeQuotaReader(switched, home).Read(CancellationToken.None)).Accounts.Length == 0, "ZCode discards delayed usage after encrypted credentials change");
            await File.WriteAllTextAsync(file, credentials);
            using var denied = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-error-body") }));
            var failure = await new LocalZCodeQuotaReader(denied, home).Read(CancellationToken.None);
            check(failure.Status == "unauthorized" && !JsonSerializer.Serialize(failure).Contains("secret-error"), "ZCode credential errors remain sanitized");
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
    }
    private static string Encrypt(byte[] key, string value)
    {
        var iv = RandomNumberGenerator.GetBytes(12); var clear = Encoding.UTF8.GetBytes(value); var encrypted = new byte[clear.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key, 16); aes.Encrypt(iv, clear, encrypted, tag);
        string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "enc:v1:" + B64(iv) + "." + B64(tag) + "." + B64(encrypted);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request)); }
}
