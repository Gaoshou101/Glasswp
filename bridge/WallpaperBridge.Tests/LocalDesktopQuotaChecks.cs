using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WallpaperBridge;

static class LocalDesktopQuotaChecks
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
    private static string Jwt(string sub, long? expires = null) => "header." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { sub, exp = expires ?? DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    private static HttpResponseMessage Json(object body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static void Write(string path, object body) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(body)); }
    public static async Task Run(Action<bool, string> check)
    {
        var report = check; var checkGate = new object(); check = (passed, message) => { lock (checkGate) report(passed, message); };
        var home = Path.Combine(Path.GetTempPath(), "wallpaper-desktop-agents-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
        var cursorPath = Path.Combine(home, "AppData", "Roaming", "Cursor", "auth.json");
        var teamPath = Path.Combine(home, ".cursor", "cli-config.json");
        var wbPath = Path.Combine(home, "AppData", "Local", "CodeBuddyExtension", "Data", "Public", "auth", "workbuddy-desktop.info");
        var wbAiPath = Path.Combine(Path.GetDirectoryName(wbPath)!, "workbuddy-desktop-ai.info");
        var traePath = Path.Combine(home, "AppData", "Roaming", "Trae CN", "User", "globalStorage", "storage.json");
        var soloPath = Path.Combine(home, "AppData", "Roaming", "TRAE SOLO CN", "User", "globalStorage", "storage.json");
        var cursorToken = Jwt("private-cursor-user"); var traeToken = Jwt("private-trae-user");
        var requests = new List<string>(); var mode = "normal";
        using var client = new HttpClient(new Handler(async request => {
            var uri = request.RequestUri!; lock (requests) requests.Add(uri.Host + uri.AbsolutePath);
            check(request.Method == HttpMethod.Post && !uri.AbsolutePath.Contains("refresh") && !uri.AbsolutePath.Contains("chat"), "desktop readers only call read-only plan and quota methods");
            if (uri.Host == "api2.cursor.sh") {
                check(request.Headers.Authorization?.Parameter == cursorToken, "Cursor sends native access token to fixed vendor host");
                if (mode != "mismatch-team") check(request.Headers.GetValues("x-cursor-team-id").Single() == "123", "Cursor active team header is scoped to matching JWT identity");
                else check(!request.Headers.Contains("x-cursor-team-id"), "Cursor skips a stale other-account active team");
                if (mode == "malformed") return Json(new { });
                if (uri.AbsolutePath.EndsWith("GetPlanInfo")) return Json(new { planInfo = new { planName = mode == "sensitive-plan" ? cursorToken + " private-user@example.test" : mode == "future-plan" ? "Cursor Future Plan 42" : "Pro+", includedUsagePeriod = 2 }, isMerged = mode == "merged" });
                if (mode == "switch") Write(cursorPath, new { accessToken = Jwt("changed-user") });
                return Json(new { planUsage = new { autoPercentUsed = 0, apiPercentUsed = 100, totalPercentUsed = 25 }, billingCycleEnd = DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeMilliseconds().ToString() });
            }
            if (uri.Host is "copilot.tencent.com" or "www.workbuddy.ai") {
                check(request.Headers.GetValues("X-User-Id").Single() == "private-wb-user", "WorkBuddy billing carries native account identity");
                if (mode == "unauthorized") return Json(new { }, HttpStatusCode.Unauthorized);
                if (mode == "malformed") return Json(new { code = 0, data = new { unexpected = true } });
                if (mode == "groups") return Json(new { code = 0, data = new { IsPaidUser = true, CreditGroups = new[] { new { CycleTotalCapacity = "800", CycleUsedCapacity = "200" } }, Packages = new[] { new { CycleTotalCapacity = "10000", CycleUsedCapacity = "0" } } } });
                if (uri.AbsolutePath.EndsWith("get-enterprise-user-usage")) return Json(new { code = 0, data = new { limitNum = "500", credit = "125" } });
                if (mode == "enterprise") return Json(new { code = 0, data = new { Packages = new object[] { } } });
                return Json(new { code = 0, data = new { IsPaidUser = true, Packages = new[] { new { CycleTotalCapacity = "10000", CycleUsedCapacity = "2500" } } } });
            }
            check(uri.AbsoluteUri == "https://api.trae.cn/trae/api/v2/pay/ide_user_ent_usage", "Trae usage is restricted to the fixed CN billing endpoint");
            check(request.Headers.Authorization?.Scheme == "Cloud-IDE-JWT" && request.Headers.Authorization.Parameter == traeToken, "Trae uses native login JWT without exchanging tokens");
            var body = await request.Content!.ReadAsStringAsync(); check(body.Contains("require_usage") && !body.Contains("refresh"), "Trae only requests entitlement usage");
            if (mode == "malformed") return Json(new { code = 0, data = new { } });
            return Json(new { code = 0, is_credits_billing = true, user_entitlement_pack_list = new[] { new { entitlement_base_info = new { quota = new { credits_limit = mode == "unlimited" ? -1 : 1000 }, end_time = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() }, usage = new { credits_amount = 200 } } } });
        }));
        try {
            var reader = new LocalDesktopQuotaReader(client, home);
            var absent = await reader.Read(CancellationToken.None);
            check(absent.Status == "unconfigured" && absent.Accounts.Length == 0 && requests.Count == 0, "missing desktop installs make no network requests");
            Write(cursorPath, new { accessToken = cursorToken, refreshToken = "never-use-cursor-refresh" });
            Write(teamPath, new { authInfo = new { authId = "private-cursor-user", activeTeamId = 123 } });
            Write(wbPath, new { auth = new { accessToken = "private-wb-access", refreshToken = "never-use-wb-refresh", expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() }, account = new { uid = "private-wb-user" } });
            Write(wbAiPath, new { auth = new { accessToken = "private-wb-access", expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() }, account = new { uid = "private-wb-user" } });
            var traeLogin = JsonSerializer.Serialize(new { token = traeToken, userId = "private-trae-user", refreshToken = "never-use-trae-refresh" });
            var traeDoc = new Dictionary<string, string> { ["iCubeAuthInfo://icube.cloudide"] = traeLogin, ["iCubeAuthInfo://icube-dc:1234567890123456"] = "private-device-key", ["telemetry.machineId"] = "private-machine-id" };
            Write(traePath, traeDoc); Write(soloPath, traeDoc);
            var all = await reader.Read(CancellationToken.None);
            check(all.Status == "ok" && all.Accounts.Length == 4 && all.Accounts.Count(a => a.Provider == "trae-cn") == 1, "native desktop accounts aggregate while identical Trae/SOLO logins deduplicate");
            var cursor = all.Accounts.Single(a => a.Provider == "cursor"); var wb = all.Accounts.Single(a => a.Provider == "workbuddy"); var trae = all.Accounts.Single(a => a.Provider == "trae-cn");
            check(cursor.Plan == "Pro+" && cursor.Windows.Select(w => w.RemainingPercent).SequenceEqual(new double?[] { 100, 0, 75 }), "Cursor exact zero and exhausted quotas preserved");
            check(wb.Plan == "Pro" && wb.Windows.Single().UsedValue == 2500 && wb.Windows.Single().RemainingPercent == 75, "WorkBuddy capacities parse numeric strings and compute remaining credits");
            check(trae.Plan == "Credits" && trae.Windows.Single().RemainingPercent == 80 && trae.Windows.Single().ResetAt is not null, "Trae entitlement credits and reset map to standard windows");
            var safe = JsonSerializer.Serialize(all);
            check(!safe.Contains("private-") && !safe.Contains("never-use") && !safe.Contains(home), "desktop output omits credentials, raw identities, device IDs, and file paths");
            mode = "sensitive-plan"; var sensitivePlan = await reader.Read(CancellationToken.None); var sensitiveJson = JsonSerializer.Serialize(sensitivePlan);
            check(sensitivePlan.Accounts.Single(a => a.Provider == "cursor").Plan == "" && !sensitiveJson.Contains(cursorToken) && !sensitiveJson.Contains("private-user@example.test"), "Cursor unknown plan text cannot publish an echoed token or email");
            mode = "future-plan"; var futurePlan = await reader.Read(CancellationToken.None);
            check(futurePlan.Accounts.Single(a => a.Provider == "cursor").Plan == "Cursor Future Plan 42", "Cursor normal future plan names remain readable without a fixed tier allowlist");
            mode = "unlimited"; var unlimited = await reader.Read(CancellationToken.None);
            check(unlimited.Accounts.Single(a => a.Provider == "trae-cn") is { Plan: "Unlimited", Windows: [{ RemainingPercent: null, LimitValue: null, Mode: "non_window", DisplayText: "不限量", UsedValue: 200 }] }, "Trae explicit unlimited entitlement remains a display-only evidence window without a fabricated percentage");
            mode = "normal";
            traeDoc["iCubeAuthInfo://icube.cloudide"] = Convert.ToBase64String(EncodeTrae(traeLogin)); Write(traePath, traeDoc);
            var nativeEncrypted = await reader.Read(CancellationToken.None);
            check(nativeEncrypted.Accounts.Single(a => a.Provider == "trae-cn").Windows.Single().RemainingPercent == 80, "encrypted native Trae login reads entitlement and deduplicates its plaintext SOLO sibling");
            mode = "merged"; var merged = await reader.Read(CancellationToken.None);
            check(merged.Accounts.Single(a => a.Provider == "cursor").Windows.Single() is { RemainingPercent: 75, DurationSeconds: 604800 }, "merged Cursor plan has one weekly window with exact remaining quota");
            mode = "groups"; var grouped = await reader.Read(CancellationToken.None);
            check(grouped.Accounts.Single(a => a.Provider == "workbuddy").Windows.Single().LimitValue == 800, "WorkBuddy grouped credits do not double-count package totals");
            mode = "malformed"; var malformed = await reader.Read(CancellationToken.None);
            check(malformed.Accounts.Length == 0 && malformed.Status == "offline", "unexpected vendor response never fabricates a Free plan or valid empty account");
            mode = "mismatch-team"; Write(teamPath, new { authInfo = new { authId = "some-other-user", activeTeamId = 9 } }); await reader.Read(CancellationToken.None);
            mode = "normal"; Write(teamPath, new { authInfo = new { authId = "private-cursor-user", activeTeamId = 123 } });
            File.WriteAllText(traePath, "{broken");
            var isolated = await reader.Read(CancellationToken.None);
            check(isolated.Status == "stale" && isolated.Accounts.Length == 4, "one damaged profile cannot hide healthy SOLO or other desktop accounts");
            mode = "switch"; var switched = await reader.Read(CancellationToken.None);
            check(switched.Accounts.All(a => a.Provider != "cursor") && switched.Message.Contains("登录已切换"), "Cursor rejects an in-flight quota when its login changes");
            File.Delete(cursorPath); File.Delete(traePath); File.Delete(soloPath);
            mode = "enterprise"; Write(wbPath, new { auth = new { accessToken = "private-wb-access" }, account = new { uid = "private-wb-user", enterpriseId = "private-team" } });
            var enterprise = await reader.Read(CancellationToken.None);
            check(enterprise.Accounts.Single(a => a.Provider == "workbuddy").Windows.Single().RemainingPercent == 75, "WorkBuddy enterprise account uses its separate billing meter");
            Write(wbPath, new { auth = new { accessToken = new { wbEncrypted = 1, envelope = "private-envelope" } }, account = new { uid = "private-wb-user" } });
            var encrypted = await reader.Read(CancellationToken.None);
            check(encrypted.Status == "stale" && encrypted.Accounts.Length == 1 && encrypted.Message.Contains("加密登录存储"), "unreadable WorkBuddy encrypted login is reported while international account remains usable");
            File.Delete(wbAiPath); Write(wbPath, new { auth = new { accessToken = "private-wb-access", expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds() }, account = new { uid = "private-wb-user" } });
            requests.Clear(); var expired = await reader.Read(CancellationToken.None);
            check(expired.Status == "unauthorized" && requests.Count == 0, "expired native desktop access never starts refresh or sends quota request");
            Write(wbPath, new { auth = new { accessToken = "private-wb-access" }, account = new { uid = "private-wb-user" } }); mode = "unauthorized";
            var refused = await reader.Read(CancellationToken.None); check(refused.Status == "unauthorized" && refused.Message.Contains("原工具"), "vendor 401 directs user to original desktop login without refresh");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var cancelPropagated = false;
            try { await reader.Read(cancelled.Token); } catch (OperationCanceledException) { cancelPropagated = true; }
            check(cancelPropagated, "desktop readers propagate caller cancellation");
            CodecChecks(check);
        } finally { Directory.Delete(home, true); }
    }
    private static byte[] EncodeTrae(string text)
    {
        byte[] a = [82,9,106,213,48,54,165,56,191,64,163,158,129,243,215,251,124,227,57,130,155,47,255,135,52,142,67,68,196,222,233,203,84,123,148,50,166,194,35,61,238,76,149,11,66,250,195,78,8,46,161,102,40,217,36,178,118,91,162,73,109,139,209,37];
        byte[] b = [31,221,168,51,136,7,199,49,177,18,16,89,39,128,236,95,96,81,127,169,25,181,74,13,45,229,122,159,147,201,156,239,160,224,59,77,174,42,245,176,200,235,187,60,131,83,153,97,23,43,4,126,186,119,214,38,225,105,20,99,85,33,12,125];
        var salt = a.Select((v, i) => (byte)(v ^ b[i])).ToArray(); var random = RandomNumberGenerator.GetBytes(32);
        var key = SHA512.HashData(SHA512.HashData(random).Concat(salt).ToArray()); var body = Encoding.UTF8.GetBytes(text);
        using var aes = Aes.Create(); aes.Key = key[..16]; aes.IV = key[16..32];
        var cipher = aes.EncryptCbc(SHA512.HashData(body).Concat(body).ToArray(), aes.IV);
        return Convert.FromHexString("746305100000").Concat(random).Concat(cipher).ToArray();
    }
    private static void CodecChecks(Action<bool, string> check)
    {
        const string body = "{\"userId\":\"codec-fixture\"}"; var envelope = EncodeTrae(body);
        check(LocalDesktopQuotaReader.DecodeTrae(Convert.ToBase64String(envelope)) == body, "Trae native AES codec opens a valid integrity-checked envelope");
        // Independently produced with Node crypto against upstream's second header/salt.
        const string secondEnvelope = "EjkgIAIDBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBweb7jGyHhs4OBL5j4GVb1phXiqlPTtgnsTC5Udd5eubYMs4stSqdWkt/7MZ/CK54JqRQugCtv99p59C8YIXZJp8tEM5AfnIzERBoiVOtf6kG+kGfvuispWh0/HdH/c+45k=";
        check(LocalDesktopQuotaReader.DecodeTrae(secondEnvelope) == body, "Trae second native codec variant decodes an independent fixture");
        envelope[^1] ^= 1; var rejected = false;
        try { LocalDesktopQuotaReader.DecodeTrae(Convert.ToBase64String(envelope)); } catch (CryptographicException) { rejected = true; }
        check(rejected, "Trae native codec rejects tampered ciphertext");
    }
}
