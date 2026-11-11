using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WallpaperBridge;

internal static class LocalDesktopWalletChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        JsonElement Data(string s) { using var doc = JsonDocument.Parse(s); return doc.RootElement.Clone(); }
        var now = DateTimeOffset.UtcNow;
        var points = LocalTeleAgentQuotaReader.ParsePoints(Data("""{"pointsBalance":"1200","groups":[{"active":[{"effectiveEnd":"2099-01-01T00:00:00Z"}]}]}"""), now);
        check(points?.RemainingValue == 1200 && points.RemainingPercent is null && points.ResetAt?.Year == 2099, "TeleAgent wallet is balance, never percentage");
        check(LocalTeleAgentQuotaReader.ParsePoints(Data("{}"), now) is null, "TeleAgent missing balance remains unknown");
        var wallets = LocalAutoClawQuotaReader.ParseWallet(Data("""{"total_balance":1000,"wallets":[{"display":true,"balance":500,"public_wallet_type":"reward"},{"balance":500},{"display":true,"balance":0,"public_wallet_type":"daily"}]}"""), now);
        check(wallets.Length == 2 && wallets[0].RemainingValue == 500 && wallets[1].RemainingValue == 0 && wallets.All(w => w.RemainingPercent is null), "AutoClaw only shows visible wallets and preserves zero");
        var home = Path.Combine(Path.GetTempPath(), "wallpaper-desktop-wallet-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
        try
        {
            var root = Path.Combine(home, ".local", "share", "TeleAgent"); var scope = "v1_public_12345";
            var tokenDir = Path.Combine(root, "users", scope, "app-auth"); Directory.CreateDirectory(tokenDir);
            var tokenFile = Path.Combine(tokenDir, "token.json"); const string tokenContent = "{\"encryptedToken\":\"fixture-not-exported\"}"; await File.WriteAllTextAsync(tokenFile, tokenContent);
            var cookieDir = Path.Combine(root, "Partitions", "owner%3A" + scope, "Network"); Directory.CreateDirectory(cookieDir);
            using (var db = new SqliteConnection("Data Source=" + Path.Combine(cookieDir, "Cookies") + ";Pooling=false"))
            {
                await db.OpenAsync(); using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TABLE meta(key TEXT,value TEXT); INSERT INTO meta VALUES('version','24'); CREATE TABLE cookies(name TEXT,value TEXT,host_key TEXT,encrypted_value BLOB); INSERT INTO cookies VALUES('operationToken','private-operation','agent.teleai.com.cn',X''); INSERT INTO cookies VALUES('operToken','private-oper','agent.teleai.com.cn',X''); INSERT INTO cookies VALUES('operationToken','wrong-domain','unrelated.example',X'')"; await cmd.ExecuteNonQueryAsync();
            }
            var calls = 0;
            using var client = new HttpClient(new Handler(request =>
            {
                calls++; check(request.Method == HttpMethod.Get && request.RequestUri!.Host == "agent.teleai.com.cn" && request.RequestUri.AbsolutePath is "/meter/super-agent/user/portal/points/detail" or "/meter/super-agent/user/portal/companyStore/userCenter", "TeleAgent only queries scoped points and user-center GET");
                check(request.Headers.GetValues("X-Token").Single() == "private-operation" && request.Headers.GetValues("userId").Single() == "12345", "TeleAgent matches cookie to account scope");
                return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("detail") ? "{\"code\":200,\"data\":{\"pointsBalance\":1200}}" : "{\"code\":200,\"data\":{\"plan_display_name\":\"Pro\"}}") };
            }));
            var result = await new LocalTeleAgentQuotaReader(client, home).Read(CancellationToken.None);
            check(result.Status == "ok" && result.Accounts.Single().Plan == "Pro" && calls == 2, "TeleAgent local cookie discovery reads points and plan");
            check(await File.ReadAllTextAsync(tokenFile) == tokenContent && !JsonSerializer.Serialize(result).Contains("private"), "TeleAgent does not change or export desktop credentials");
            using (var switched = new HttpClient(new Handler(request =>
            {
                File.WriteAllText(tokenFile, tokenContent.Replace("fixture-not-exported", "replacement-envelope"));
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":200,\"data\":{\"pointsBalance\":1200}}") };
            })))
                check((await new LocalTeleAgentQuotaReader(switched, home).Read(CancellationToken.None)).Accounts.Length == 0, "TeleAgent discards delayed wallet after native account session changes");
            await File.WriteAllTextAsync(tokenFile, tokenContent);

            var autoRoot = Path.Combine(home, "AppData", "Roaming", "AutoClaw-official"); var accountDir = Path.Combine(autoRoot, "accounts", "fixture"); Directory.CreateDirectory(accountDir);
            var key = RandomNumberGenerator.GetBytes(32); var wrapped = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
            await File.WriteAllTextAsync(Path.Combine(autoRoot, "Local State"), JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = Convert.ToBase64String("DPAPI"u8.ToArray().Concat(wrapped).ToArray()) } }));
            var clear = Encoding.UTF8.GetBytes("{\"accessToken\":\"private-access\",\"refreshToken\":\"do-not-use\",\"userId\":\"private-user\"}");
            var iv = RandomNumberGenerator.GetBytes(12); var encrypted = new byte[clear.Length]; var tag = new byte[16]; using (var aes = new AesGcm(key, 16)) aes.Encrypt(iv, clear, encrypted, tag);
            var payload = "v10"u8.ToArray().Concat(iv).Concat(encrypted).Concat(tag).ToArray(); var credFile = Path.Combine(accountDir, "account-credentials.enc"); await File.WriteAllBytesAsync(credFile, payload);
            check(LocalTeleAgentQuotaReader.DecryptGcm(payload, key).SequenceEqual(clear), "desktop safeStorage AES-GCM authenticates fixture");
            var autoCalls = 0;
            using var autoClient = new HttpClient(new Handler(request =>
            {
                autoCalls++; check(request.Method == HttpMethod.Get && request.RequestUri!.Host == "autoglm-acceleration-api.zhipuai.cn" && request.RequestUri.AbsolutePath == "/agent-assetmgr/api/v2/wallets", "AutoClaw only queries wallet, never refreshes session");
                var ts = request.Headers.GetValues("X-Auth-TimeStamp").Single();
                var expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("100003&" + ts + "&38d2391985e2369a5fb8227d8e6cd5e5"))).ToLowerInvariant();
                check(request.Headers.GetValues("X-Auth-Sign").Single() == expected && request.Headers.GetValues("Authorization").Single() == "Bearer private-access", "AutoClaw wallet auth matches native protocol");
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"data\":{\"total_balance\":500,\"wallets\":[{\"display\":true,\"balance\":500}]}}") };
            }));
            var auto = await new LocalAutoClawQuotaReader(autoClient, home).Read(CancellationToken.None);
            check(auto.Status == "ok" && auto.Accounts.Single().Windows[0].RemainingValue == 500 && autoCalls == 1, "AutoClaw discovers current-user DPAPI desktop credential");
            check((await File.ReadAllBytesAsync(credFile)).SequenceEqual(payload) && !JsonSerializer.Serialize(auto).Contains("private"), "AutoClaw leaves session file and secrets unchanged");
            using (var switched = new HttpClient(new Handler(_ =>
            {
                File.WriteAllBytes(credFile, [1, 2, 3]);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"data\":{\"total_balance\":500}}") };
            })))
                check((await new LocalAutoClawQuotaReader(switched, home).Read(CancellationToken.None)).Accounts.Length == 0, "AutoClaw discards delayed balance after encrypted session changes");
            await File.WriteAllBytesAsync(credFile, payload);
            using var expiredClient = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":410000,\"msg\":\"sensitive-error\"}") }));
            var expired = await new LocalAutoClawQuotaReader(expiredClient, home).Read(CancellationToken.None);
            check(expired.Status == "unauthorized" && !expired.Message.Contains("sensitive-error"), "AutoClaw rejected wallet does not rotate desktop refresh token");
            await File.WriteAllBytesAsync(credFile, [1, 2, 3]);
            check((await new LocalAutoClawQuotaReader(autoClient, home).Read(CancellationToken.None)).Status == "offline" && autoCalls == 1, "AutoClaw unreadable native session reports failure without a request");
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request)); }
}
