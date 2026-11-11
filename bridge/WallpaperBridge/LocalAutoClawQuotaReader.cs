using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static WallpaperBridge.LocalKiroQuotaReader;

namespace WallpaperBridge;

// providers-hub 0.12.3 desktop discovery. Deliberately never rotates tokens (would revoke desktop login).
internal sealed class LocalAutoClawQuotaReader(HttpClient client, string? userHome = null)
{
    private sealed record Site(string Id, string Directory, string Origin, string Name);
    private static readonly Site[] Sites = [new("autoclaw", "AutoClaw-oversea-official", "https://autoglm-api.autoglm.ai", "AutoClaw 国际版本机"), new("autoclaw-cn", "AutoClaw-official", "https://autoglm-acceleration-api.zhipuai.cn", "AutoClaw 国内版本机")];
    private sealed record Login(string Access, string Identity, string File, string Fingerprint);
    public async Task<QuotaData> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var accounts = new List<QuotaAccount>(); var discovered = 0; var failed = false; var expired = false;
            foreach (var site in Sites)
            {
                var detected = CandidateFiles(site).Count();
                var logins = await Logins(site, timeout.Token);
                discovered += detected;
                failed |= detected > logins.Count;
                foreach (var login in logins.DistinctBy(x => x.Identity))
                {
                    try
                    {
                        var data = await Wallet(site, login.Access, timeout.Token);
                        var now = DateTimeOffset.UtcNow; var windows = ParseWallet(data, now);
                        if (windows.Length == 0) { failed = true; continue; }
                        var after = (await Logins(site, timeout.Token)).FirstOrDefault(x => x.File == login.File);
                        if (after?.Fingerprint != login.Fingerprint) { failed = true; continue; }
                        accounts.Add(new("local:" + site.Id + ":" + Hash(login.Identity), site.Name, site.Id, "", windows));
                    }
                    catch (HttpRequestException e) { expired |= e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden; failed = true; }
                    catch (JsonException) { failed = true; }
                }
            }
            return new(accounts.Count > 0 ? failed ? "stale" : "ok" : discovered == 0 ? "unconfigured" : expired ? "unauthorized" : "offline", accounts.Count > 0 ? DateTimeOffset.UtcNow : null,
                accounts.Count > 0 ? failed ? "AutoClaw：已读取部分本机积分" : "AutoClaw：已读取本机登录积分"
                    : discovered == 0 ? "AutoClaw：未发现可读的本地登录" : "AutoClaw：钱包登录不可用，请打开原客户端更新登录后重试", accounts.ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException or HttpRequestException or OperationCanceledException or FormatException or ArgumentException)
        { return new("offline", null, "AutoClaw：本地登录或积分暂不可读取", []); }
    }
    private async Task<List<Login>> Logins(Site site, CancellationToken token)
    {
        var appData = userHome is null ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : Path.Combine(userHome, "AppData", "Roaming");
        var root = Path.Combine(appData, site.Directory); var accounts = Path.Combine(root, "accounts"); var result = new List<Login>();
        if (!Directory.Exists(accounts)) return result;
        foreach (var directory in Directory.EnumerateDirectories(accounts).Take(32))
        {
            var file = Path.Combine(directory, "account-credentials.enc"); if (!File.Exists(file)) continue;
            try
            {
                if (new FileInfo(file).Length > 1024 * 1024) continue;
                var encrypted = await File.ReadAllBytesAsync(file, token);
                var clear = await LocalTeleAgentQuotaReader.DecryptWindows(encrypted, root, token);
                using var doc = JsonDocument.Parse(clear, new JsonDocumentOptions { MaxDepth = 32 }); var data = doc.RootElement;
                var access = Text(data, "accessToken"); if (access.Length == 0) access = Text(data, "access_token"); if (access.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) access = access[7..];
                if (access.Length == 0) continue;
                var identity = Field(data, "userId").ToString(); if (identity.Length == 0) identity = Field(data, "user_id").ToString(); if (identity.Length == 0) identity = "token:" + access;
                result.Add(new(access, identity, file, Hash(access + "\0" + identity)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException) { }
        }
        return result;
    }
    private IEnumerable<string> CandidateFiles(Site site)
    {
        var appData = userHome is null ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : Path.Combine(userHome, "AppData", "Roaming");
        var directory = Path.Combine(appData, site.Directory, "accounts");
        return Directory.Exists(directory) ? Directory.EnumerateDirectories(directory).Take(32).Select(d => Path.Combine(d, "account-credentials.enc")).Where(File.Exists) : [];
    }
    private async Task<JsonElement> Wallet(Site site, string access, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, site.Origin + "/agent-assetmgr/api/v2/wallets?biz_app_id=autoclaw");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("100003&" + ts + "&38d2391985e2369a5fb8227d8e6cd5e5"))).ToLowerInvariant();
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + access);
        request.Headers.UserAgent.ParseAdd("AutoClaw/1.18.1 (Windows-x64)");
        foreach (var (key, value) in new Dictionary<string, string> { ["X-Version"] = "1.18.1", ["X-Tm"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), ["X-Product"] = "autoclaw", ["X-Auth-Appid"] = "100003", ["X-Auth-TimeStamp"] = ts, ["X-Auth-Sign"] = sign, ["X-Trace-Id"] = Guid.NewGuid().ToString("N"), ["X-Lang"] = "zh-CN", ["X-Channel"] = "AutoClaw4", ["Origin"] = "http://localhost:18432" }) request.Headers.Add(key, value);
        request.Headers.Referrer = new("http://localhost:18432/"); request.Headers.Accept.Add(new("*/*"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is { } finalUri && finalUri != request.RequestUri) throw new HttpRequestException("AutoClaw wallet failed", null, response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(token); var data = await Json(stream, token);
        if (Field(data, "code").ToString() == "410000") throw new HttpRequestException("AutoClaw wallet login expired", null, HttpStatusCode.Unauthorized);
        if (Field(data, "code").ToString() is not ("" or "0")) throw new JsonException(); return Field(data, "data");
    }
    internal static QuotaWindow[] ParseWallet(JsonElement data, DateTimeOffset observed)
    {
        var windows = new List<QuotaWindow>(); var i = 0;
        foreach (var wallet in Array(data, "wallets").Take(64).Where(w => Field(w, "display").ValueKind == JsonValueKind.True).OrderBy(w => Num(w, "priority") ?? 100))
        {
            var balance = Num(wallet, "balance"); if (balance is null) continue;
            var name = Safe(Text(wallet, "display_name")); if (name.Length == 0) name = Text(wallet, "public_wallet_type") switch { "reward" => "奖励积分", "daily" => "每日积分", "subscription" => "签约积分", "fuel_pack" => "加油包积分", _ => "其他积分" };
            windows.Add(new("autoclaw:wallet:" + i++, name, null, null, null, "积分", null, null, "balance", "exact", observed, false, RemainingValue: balance));
        }
        if (windows.Count == 0 && Num(data, "total_balance") is { } total) windows.Add(new("autoclaw:wallet:total", "积分余额", null, null, null, "积分", null, null, "balance", "exact", observed, false, RemainingValue: total));
        return windows.ToArray();
    }
}
