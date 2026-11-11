using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WallpaperBridge;

// Reads only the three applications' own session files; never opens browser profiles or cookies.
internal sealed class LocalEncryptedQuotaReader(HttpClient client, string? userHome = null,
    Func<byte[], byte[]>? unprotect = null, Func<IEnumerable<string>>? studioRoots = null)
{
    private sealed record Login(string Provider, string Path, string Fingerprint, string Token, string Identity, string Cookie, bool Expired);
    private string Home => userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string Local => userHome is null ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : Path.Combine(Home, "AppData", "Local");
    private string Roaming => userHome is null ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : Path.Combine(Home, "AppData", "Roaming");
    private byte[] Unprotect(byte[] bytes) => unprotect?.Invoke(bytes) ?? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
    private static string Text(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
    private static JsonElement Field(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) ? value : default;
    private static double? Number(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object ? QuotaAdapter.Number(node, key) : null;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static JsonElement Json(byte[] bytes)
    { using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException(); return document.RootElement.Clone(); }
    private static JsonElement Claims(string jwt)
    {
        try
        {
            var parts = jwt.Split('.'); if (parts.Length != 3 || parts[1].Length > 32768) return default;
            var body = parts[1].Replace('-', '+').Replace('_', '/'); body = body.PadRight(body.Length + (4 - body.Length % 4) % 4, '='); return Json(Convert.FromBase64String(body));
        }
        catch (Exception e) when (e is JsonException or FormatException) { return default; }
    }
    private static bool Expired(string jwt) => Claims(jwt) is { ValueKind: JsonValueKind.Object } claims && Number(claims, "exp") is { } seconds && seconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static async Task<byte[]?> FileBytes(string path, CancellationToken token)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 1024 * 1024) throw new IOException();
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await file.ReadAsync(buffer, token)) > 0)
            { if (output.Length + count > 1024 * 1024) throw new IOException(); output.Write(buffer, 0, count); }
            return output.ToArray();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
    private IEnumerable<string> StudioRoots()
    {
        if (studioRoots is not null) foreach (var root in studioRoots()) yield return root;
        if (userHome is null)
        {
            var configured = Environment.GetEnvironmentVariable("ASTUDIO_HOME") ?? Environment.GetEnvironmentVariable("SYNARA_HOME");
            if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\AStudioStorage", writable: false);
            foreach (var prefix in new[] { "Production", "Canary", "Development" })
                if (key?.GetValue(prefix + "DataRoot") is string { Length: > 0 } root) yield return root;
        }
        yield return Path.Combine(Home, ".acode", "acode");
    }
    private async Task<Login?> LoginFor(string provider, CancellationToken token)
    {
        string path; byte[]? bytes; JsonElement session; string access; string identity; string cookie = ""; string fingerprint;
        if (provider == "warp")
        {
            path = Path.Combine(Local, "warp", "Warp", "data", "dev.warp.Warp-User"); bytes = await FileBytes(path, token); if (bytes is null) return null;
            session = Json(Unprotect(bytes)); access = Text(Field(session, "id_token"), "id_token"); identity = Text(session, "local_id"); fingerprint = Hash(bytes);
            if (identity.Length == 0) identity = Text(Claims(access), "sub");
        }
        else if (provider == "agnes-code")
        {
            var directory = Path.Combine(Roaming, "AgnesCode"); path = Path.Combine(directory, "code-auth-session.v1"); bytes = await FileBytes(path, token); if (bytes is null) return null;
            var state = await FileBytes(Path.Combine(directory, "Local State"), token); if (state is null) throw new JsonException();
            var wrapped = Convert.FromBase64String(Text(Field(Json(state), "os_crypt"), "encrypted_key"));
            if (wrapped.Length <= 5 || !wrapped.AsSpan(0, 5).SequenceEqual("DPAPI"u8)) throw new CryptographicException();
            var key = Unprotect(wrapped[5..]);
            try
            {
                if (key.Length != 32 || bytes.Length < 31 || !bytes.AsSpan(0, 3).SequenceEqual("v10"u8)) throw new CryptographicException();
                var plain = new byte[bytes.Length - 31]; using var aes = new AesGcm(key, 16);
                aes.Decrypt(bytes.AsSpan(3, 12), bytes.AsSpan(15, bytes.Length - 31), bytes.AsSpan(bytes.Length - 16), plain);
                try { session = Json(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            access = Text(session, "accessToken"); identity = Text(Claims(access), "sub");
            if (identity.Length == 0) identity = Text(Field(session, "userInfo"), "username"); fingerprint = Hash(bytes) + Hash(state);
        }
        else
        {
            foreach (var root in StudioRoots().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                path = Path.Combine(root, "userdata", "astron-session.json"); bytes = await FileBytes(path, token); if (bytes is null) continue;
                session = Json(bytes); if (Field(session, "banned").ValueKind == JsonValueKind.True) throw new HttpRequestException(null, null, HttpStatusCode.Forbidden);
                var web = Text(session, "token"); var account = Text(session, "accountId"); var sso = Text(session, "ssoSessionId");
                access = Text(session, "modelBearerToken"); if (access.Length == 0) access = web;
                if (access.Length == 0 || web.Length == 0 || account.Length == 0) continue;
                if (new[] { web, account, sso }.Any(value => value.Length > 8192 || value.Any(c => char.IsControl(c) || c == ';'))) throw new JsonException();
                cookie = (sso.Length > 0 ? "ssoSessionId=" + sso + "; sso_sessionid=" + sso + "; " : "") + "account_id=" + account + "; token=" + web;
                identity = Text(session, "uid"); if (identity.Length == 0) identity = account;
                return new(provider, path, Hash(bytes), access, identity, cookie, Expired(web));
            }
            return null;
        }
        if (access.Length == 0) throw new JsonException(); if (identity.Length == 0) identity = "token:" + access;
        return new(provider, path, fingerprint, access, identity, cookie, Expired(access));
    }
    public async Task<QuotaData> Read(CancellationToken token)
    {
        var results = await Task.WhenAll(new[] { "warp", "agnes-code", "astudio" }.Select(provider => ReadProvider(provider, token)));
        var available = results.Where(result => result.Status != "unconfigured").ToArray();
        var ok = available.Count(result => result.Status == "ok");
        return new(available.Length == 0 ? "unconfigured" : ok == available.Length ? "ok" : ok > 0 ? "stale" : available.Any(result => result.Status == "unauthorized") ? "unauthorized" : "offline",
            ok > 0 ? DateTimeOffset.UtcNow : null, string.Join("；", results.Select(result => result.Message)), available.SelectMany(result => result.Accounts).ToArray());
    }
    private async Task<QuotaData> ReadProvider(string provider, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var alias = provider switch { "warp" => "Warp", "agnes-code" => "Agnes Code", _ => "AStudio" }; Login? login = null;
        try
        {
            login = await LoginFor(provider, token); if (login is null) return new("unconfigured", null, alias + "：未发现可读取的桌面端登录", []);
            if (login.Expired) return new("unauthorized", null, alias + "：登录已过期，请打开原客户端重新登录", []);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var observed = DateTimeOffset.UtcNow; string plan = ""; QuotaWindow[] windows;
            if (provider == "warp")
            {
                const string query = "query GetRequestLimitInfo($requestContext: RequestContext!) { user(requestContext: $requestContext) { ... on UserOutput { user { requestLimitInfo { isUnlimited requestsUsedSinceLastRefresh requestLimit nextRefreshTime requestLimitRefreshDuration } bonusGrants { requestCreditsGranted requestCreditsRemaining expiration } } } } }";
                var response = await Request("https://app.warp.dev/graphql/v2?op=GetRequestLimitInfo", login, new { query, operationName = "GetRequestLimitInfo", variables = new { requestContext = new { clientContext = new { version = "v0.2026.09.02.08.27.stable_01" }, osContext = new { category = "Windows", name = "Windows", version = Environment.OSVersion.VersionString } } } }, timeout.Token);
                windows = WarpWindows(Field(Field(Field(response, "data"), "user"), "user"), observed);
            }
            else if (provider == "agnes-code")
            {
                var balance = Field(await Request("https://api.agnes-ai.com/api/v2/subscription/credits-balance", login, null, timeout.Token), "data");
                if (Number(balance, "total_balance") is not { } total || total < 0) throw new JsonException();
                plan = Safe(Text(balance, "level_name"), login); windows = [Balance("credits", "积分", total, observed)];
                foreach (var (key, label) in new[] { ("time_sensitive_balance", "限时积分"), ("permanent_balance", "永久积分") })
                    if (Number(balance, key) is > 0 and var amount) windows = [..windows, Balance(key, label, amount, observed)];
            }
            else
            {
                async Task<JsonElement> Optional(string path)
                {
                    try { return await Studio(path, login, timeout.Token); }
                    catch (HttpRequestException e) when (e.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) { return default; }
                    catch (JsonException) { return default; }
                }
                var balanceTask = Optional("points/balance"); var membershipTask = Optional("membership/me");
                await Task.WhenAll(balanceTask, membershipTask); var balance = await balanceTask; var membership = await membershipTask;
                plan = Safe(Text(membership, "planName"), login); var list = new List<QuotaWindow>();
                foreach (var (key, label) in new[] { ("totalBalance", "通用积分"), ("sparkTotalBalance", "星火积分") })
                    if (Number(balance, key) is >= 0 and var amount) list.Add(Balance(key, label, amount, observed));
                windows = list.ToArray(); if (windows.Length == 0 && plan.Length == 0) throw new JsonException();
            }
            var current = await LoginFor(provider, timeout.Token);
            if (current is null || current.Fingerprint != login.Fingerprint || current.Identity != login.Identity)
                return new("unconfigured", null, alias + "：登录已切换，等待重新读取", []);
            return new("ok", observed, alias + "：已读取桌面端套餐和额度", [new("local:" + provider + ":" + Hash(login.Identity), alias + " 本机", provider, plan, windows)]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { return new("unauthorized", null, alias + "：登录或订阅权限不可用", []); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException or HttpRequestException or OperationCanceledException or FormatException or ArgumentException or System.Security.SecurityException)
        { return new("offline", null, alias + "：本地登录或额度暂不可用", []); }
    }
    private static string Safe(string value, Login login)
    {
        var secrets = new[] { login.Token, login.Identity, login.Cookie }.Concat(login.Cookie.Split(';').Select(part => part[(part.IndexOf('=') + 1)..].Trim()));
        return value.Length <= 80 && !value.Any(char.IsControl) && !value.Contains('@') && !value.Contains("://", StringComparison.Ordinal) &&
            !value.Contains("www.", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(value, @"(?:eyJ[A-Za-z0-9_-]*\.|\b(?:sk-|ghp_|gho_|github_pat_))", RegexOptions.CultureInvariant) &&
            !secrets.Any(secret => secret.Length > 0 && value.Contains(secret, StringComparison.Ordinal)) ? value : "";
    }
    private static QuotaWindow Balance(string id, string label, double balance, DateTimeOffset observed) => new(id,
        label, null, null, null, "points", null, null, "non_window", "exact", observed, false, RemainingValue: balance);
    private async Task<JsonElement> Request(string url, Login login, object? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        if (login.Provider == "astudio")
        { request.Headers.Add("Cookie", login.Cookie); request.Headers.Add("clientType", "21"); request.Headers.Add("studioVersion", "3.5.5"); }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        if (login.Provider == "warp") { request.Headers.Add("X-Warp-Client-Version", "v0.2026.09.02.08.27.stable_01"); request.Headers.Add("X-Warp-OS-Category", "Windows"); }
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final != request.RequestUri) throw new HttpRequestException();
        using var stream = await response.Content.ReadAsStreamAsync(token); using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(bytes, token)) > 0) { if (buffer.Length + count > 1024 * 1024) throw new JsonException(); buffer.Write(bytes, 0, count); }
        return Json(buffer.ToArray());
    }
    private async Task<JsonElement> Studio(string path, Login login, CancellationToken token)
    {
        var response = await Request("https://agent.xfyun.cn/xingchen-studio/" + path, login, null, token);
        var code = Number(response, "code");
        if (code == 80000) throw new HttpRequestException(null, null, HttpStatusCode.Unauthorized);
        if (Field(response, "flag").ValueKind == JsonValueKind.False || code is not (null or 0)) throw new JsonException();
        return Field(response, "data");
    }
    internal static QuotaWindow[] WarpWindows(JsonElement user, DateTimeOffset observed)
    {
        var windows = new List<QuotaWindow>(); var info = Field(user, "requestLimitInfo");
        if (Field(info, "isUnlimited").ValueKind == JsonValueKind.True)
            windows.Add(new("requests", "请求额度", null, null, null, "requests", null, null, "non_window", "exact", observed, false, DisplayText: "不限量"));
        else if (Number(info, "requestLimit") is > 0 and var limit && Number(info, "requestsUsedSinceLastRefresh") is >= 0 and var used)
        {
            var reset = ClaudeQuotaAdapter.Reset(Field(info, "nextRefreshTime"));
            windows.Add(new("requests", "请求额度", Math.Clamp((limit - used) / limit * 100, 0, 100), used, limit, "requests", reset, null, "fixed", reset is null ? "unknown" : "exact", observed, false));
        }
        var grants = Field(user, "bonusGrants"); double granted = 0, remaining = 0;
        if (grants.ValueKind == JsonValueKind.Array)
            foreach (var grant in grants.EnumerateArray())
            {
                var until = ClaudeQuotaAdapter.Reset(Field(grant, "expiration")); if (until <= observed) continue;
                if (Number(grant, "requestCreditsRemaining") is >= 0 and var left && Number(grant, "requestCreditsGranted") is > 0 and var total)
                { remaining += left; granted += Math.Max(left, total); }
            }
        if (granted > 0) windows.Add(new("bonus", "奖励额度", remaining / granted * 100, granted - remaining, granted, "credits", null, null, "non_window", "exact", observed, false));
        if (windows.Count == 0) throw new JsonException(); return windows.ToArray();
    }
}
