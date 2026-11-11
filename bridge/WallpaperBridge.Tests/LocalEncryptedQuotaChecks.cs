using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WallpaperBridge;

static class LocalEncryptedQuotaChecks
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static string Jwt(object value) => "head." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";
    public static async Task Run(Action<bool, string> check)
    {
        var home = Path.Combine(Path.GetTempPath(), "wallpaper-encrypted-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
        var warpDir = Path.Combine(home, "AppData", "Local", "warp", "Warp", "data"); Directory.CreateDirectory(warpDir);
        var agnesDir = Path.Combine(home, "AppData", "Roaming", "AgnesCode"); Directory.CreateDirectory(agnesDir);
        var studioDir = Path.Combine(home, ".acode", "acode", "userdata"); Directory.CreateDirectory(studioDir);
        var warpPath = Path.Combine(warpDir, "dev.warp.Warp-User"); var agnesPath = Path.Combine(agnesDir, "code-auth-session.v1"); var studioPath = Path.Combine(studioDir, "astron-session.json");
        var future = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var warpAccess = Jwt(new { sub = "private-warp-account", exp = future });
        var agnesAccess = Jwt(new { sub = "private-agnes-account", exp = future });
        var studioAccess = Jwt(new { sub = "private-studio-account", exp = future }); var key = RandomNumberGenerator.GetBytes(32); var calls = 0;
        var agnesPlan = "Pro"; var studioPlan = "Premium";
        byte[] Unprotect(byte[] blob) => blob.SequenceEqual(new byte[] { 9, 8, 7 }) ? key.ToArray() : blob;
        using var http = new HttpClient(new Handler((request, _) => {
            calls++; check(request.RequestUri?.Scheme == "https" && request.RequestUri.Host is "app.warp.dev" or "api.agnes-ai.com" or "agent.xfyun.cn",
                "encrypted desktop logins target only fixed official HTTPS metadata services");
            switch (request.RequestUri!.Host)
            {
                case "app.warp.dev":
                    check(request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/graphql/v2" && request.Headers.Authorization?.Parameter == warpAccess,
                        "Warp reads request allowance through the fixed GraphQL query");
                    return Task.FromResult(Json(new { data = new { user = new { user = new { requestLimitInfo = new { isUnlimited = false, requestLimit = 100, requestsUsedSinceLastRefresh = 30, nextRefreshTime = "2099-01-01T00:00:00Z" } } } } }));
                case "api.agnes-ai.com":
                    check(request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/api/v2/subscription/credits-balance" && request.Headers.Authorization?.Parameter == agnesAccess,
                        "Agnes decrypted session is used only for credits balance");
                    return Task.FromResult(Json(new { data = new { total_balance = 900.5, time_sensitive_balance = 300, permanent_balance = 600.5, level_name = agnesPlan } }));
                default:
                    check(request.Method == HttpMethod.Get && request.Headers.GetValues("Cookie").Single().Contains("account_id=private-account") && request.Headers.GetValues("clientType").Single() == "21",
                        "AStudio uses only its desktop session cookie material for read-only membership and balance");
                    return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("membership/me")
                        ? Json(new { flag = true, code = 0, data = new { planName = studioPlan } }) : Json(new { flag = true, code = 0, data = new { totalBalance = 125.5, sparkTotalBalance = 0 } }));
            }
        }));
        var reader = new LocalEncryptedQuotaReader(http, home, Unprotect);
        try
        {
            check((await reader.Read(CancellationToken.None)).Status == "unconfigured" && calls == 0, "absent encrypted desktop sessions make no network calls or placeholder accounts");
            var warp = JsonSerializer.SerializeToUtf8Bytes(new { id_token = new { id_token = warpAccess }, local_id = "private-warp-account" }); await File.WriteAllBytesAsync(warpPath, warp);
            await File.WriteAllTextAsync(Path.Combine(agnesDir, "Local State"), JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = Convert.ToBase64String("DPAPI"u8.ToArray().Concat(new byte[] { 9, 8, 7 }).ToArray()) } }));
            var plain = JsonSerializer.SerializeToUtf8Bytes(new { accessToken = agnesAccess, userInfo = new { username = "private-username" }, bffPublicBaseUrl = "https://attacker.invalid" });
            var nonce = RandomNumberGenerator.GetBytes(12); var encrypted = new byte[plain.Length]; var tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, encrypted, tag);
            var agnes = "v10"u8.ToArray().Concat(nonce).Concat(encrypted).Concat(tag).ToArray(); await File.WriteAllBytesAsync(agnesPath, agnes);
            var studio = JsonSerializer.Serialize(new { modelBearerToken = "model-only-private", token = studioAccess, accountId = "private-account", uid = "private-studio-account", ssoSessionId = "private-sso" }); await File.WriteAllTextAsync(studioPath, studio);
            var result = await reader.Read(CancellationToken.None);
            check(result.Status == "ok" && result.Accounts.Length == 3 && calls == 4, "Warp, Agnes and AStudio desktop sessions each yield independent quota accounts");
            check(result.Accounts.Single(a => a.Provider == "warp").Windows.Single().RemainingPercent == 70, "Warp uses measured request allowance without inventing a subscription tier");
            check(result.Accounts.Single(a => a.Provider == "agnes-code").Plan == "Pro" && result.Accounts.Single(a => a.Provider == "agnes-code").Windows[0].RemainingValue == 900.5,
                "Agnes DPAPI plus AES-GCM session produces actual points balance");
            check(result.Accounts.Single(a => a.Provider == "astudio").Plan == "Premium" && result.Accounts.Single(a => a.Provider == "astudio").Windows[0].RemainingValue == 125.5,
                "AStudio subscription and point balance preserve amount semantics");
            var serialized = JsonSerializer.Serialize(result);
            check(!serialized.Contains("private-") && !serialized.Contains("attacker") && !serialized.Contains(studioAccess), "encrypted desktop snapshots export no credentials, cookie material or raw identities");
            check((await File.ReadAllBytesAsync(warpPath)).SequenceEqual(warp) && (await File.ReadAllBytesAsync(agnesPath)).SequenceEqual(agnes) && await File.ReadAllTextAsync(studioPath) == studio,
                "encrypted desktop credential files remain byte-for-byte unchanged");
            foreach (var label in new[] { "private-agnes-account", "private-studio-account", "model-only-private", "private-sso", "private-account", "private@example.test", "https://private.example.test", "eyJhbGciOiJub25lIn0.eyJzdWIiOiJhIn0.signature", "Future Plan 42" })
            {
                agnesPlan = label; studioPlan = label;
                var named = await reader.Read(CancellationToken.None);
                var agnesExpected = label == "private-agnes-account" || label.Contains('@') || label.Contains("://") || label.StartsWith("eyJ") ? "" : label;
                var studioExpected = label is "private-studio-account" or "model-only-private" or "private-sso" or "private-account" || label.Contains('@') || label.Contains("://") || label.StartsWith("eyJ") ? "" : label;
                check(named.Accounts.Single(a => a.Provider == "agnes-code").Plan == agnesExpected && named.Accounts.Single(a => a.Provider == "astudio").Plan == studioExpected,
                    "desktop plan text removes active session identity or secrets while preserving normal future labels");
            }
            agnesPlan = "Pro"; studioPlan = "Premium";
            await File.WriteAllBytesAsync(agnesPath, "broken-secret-ciphertext"u8.ToArray());
            var partial = await reader.Read(CancellationToken.None);
            check(partial.Status == "stale" && partial.Accounts.Length == 2 && !partial.Message.Contains("broken-secret"), "one broken encrypted login does not remove other providers or expose decryption contents");
            File.Delete(warpPath); File.Delete(agnesPath); File.Delete(studioPath);
            var custom = Path.Combine(home, "custom-root"); Directory.CreateDirectory(Path.Combine(custom, "userdata")); await File.WriteAllTextAsync(Path.Combine(custom, "userdata", "astron-session.json"), studio);
            check((await new LocalEncryptedQuotaReader(http, home, Unprotect, () => [custom]).Read(CancellationToken.None)).Accounts.Single().Provider == "astudio",
                "AStudio registered data roots are discoverable without reading browser profiles");
            using var switched = new HttpClient(new Handler(async (_, _) => {
                await File.WriteAllTextAsync(Path.Combine(custom, "userdata", "astron-session.json"), "{}"); return Json(new { flag = true, code = 0, data = new { totalBalance = 1, planName = "Pro" } });
            }));
            check((await new LocalEncryptedQuotaReader(switched, home, Unprotect, () => [custom]).Read(CancellationToken.None)).Accounts.Length == 0,
                "desktop session replacement discards delayed balances from the previous identity");
        }
        finally { CryptographicOperations.ZeroMemory(key); Directory.Delete(home, recursive: true); }
    }
}
