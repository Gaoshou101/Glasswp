using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using static WallpaperBridge.LocalKiroQuotaReader;

namespace WallpaperBridge;

// TeleAgent 0.2.0 protocol: account-scoped desktop cookies; no gateway/model calls or daily claim.
internal sealed class LocalTeleAgentQuotaReader(HttpClient client, string? userHome = null)
{
    private sealed record Login(string Scope, string UserId, string Operation, string Oper, string Fingerprint);
    private string Home => userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string DataRoot => userHome is null && Environment.GetEnvironmentVariable("TELEAGENT_DATA") is { Length: > 0 } configured ? Path.GetFullPath(configured) : Path.Combine(Home, ".local", "share", "TeleAgent");
    public async Task<QuotaData> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var users = Path.Combine(DataRoot, "users");
            if (!Directory.Exists(users)) return new("unconfigured", null, "TeleAgent：未发现本地桌面登录", []);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var accounts = new List<QuotaAccount>(); var failed = false; var expired = false; var discovered = 0;
            foreach (var directory in Directory.EnumerateDirectories(users).Take(32))
            {
                token.ThrowIfCancellationRequested(); var scope = Path.GetFileName(directory);
                if (!Regex.IsMatch(scope, "^v[0-9]+_public_[0-9]+$", RegexOptions.CultureInvariant) || !File.Exists(Path.Combine(directory, "app-auth", "token.json"))) continue;
                discovered++;
                try
                {
                    var login = await ReadLogin(scope, timeout.Token);
                    if (login is null) { failed = true; continue; }
                    var points = await Get(login, "/points/detail", timeout.Token);
                    var now = DateTimeOffset.UtcNow;
                    var window = ParsePoints(points, now);
                    if (window is null) { failed = true; continue; }
                    var plan = "";
                    try { plan = Safe(Text(await Get(login, "/companyStore/userCenter", timeout.Token), "plan_display_name")); }
                    catch (Exception e) when (e is HttpRequestException or JsonException) { }
                    var after = await ReadLogin(scope, timeout.Token);
                    if (after?.Fingerprint != login.Fingerprint) { failed = true; continue; }
                    accounts.Add(new("local:teleagent:" + Hash(login.UserId), "TeleAgent 本机", "teleagent", plan, [window]));
                }
                catch (HttpRequestException e) { expired |= e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden; failed = true; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException or SqliteException or FormatException) { failed = true; }
            }
            return new(accounts.Count > 0 ? failed ? "stale" : "ok" : discovered == 0 ? "unconfigured" : expired ? "unauthorized" : "offline", accounts.Count > 0 ? DateTimeOffset.UtcNow : null,
                accounts.Count > 0 ? failed ? "TeleAgent：已读取部分本机账号积分" : "TeleAgent：已读取本机账号套餐和积分"
                    : discovered == 0 ? "TeleAgent：未发现本地桌面登录" : "TeleAgent：积分凭据不可用，请切换到该账号并打开桌面版积分页", accounts.ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or OperationCanceledException or ArgumentException)
        { return new("offline", null, "TeleAgent：本地积分读取暂不可用", []); }
    }

    private async Task<Login?> ReadLogin(string scope, CancellationToken token)
    {
        var uid = scope[(scope.LastIndexOf('_') + 1)..];
        var credential = await ReadJson(Path.Combine(DataRoot, "users", scope, "app-auth", "token.json"), token);
        if (Text(credential, "encryptedToken").Length == 0) return null;
        foreach (var prefix in new[] { "owner%3A", "owner:" })
            foreach (var relative in new[] { Path.Combine("Network", "Cookies"), "Cookies" })
            {
                var path = Path.Combine(DataRoot, "Partitions", prefix + scope, relative);
                if (!File.Exists(path)) continue;
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
                await db.OpenAsync(token);
                using var command = db.CreateCommand();
                command.CommandText = "SELECT name, value, host_key, encrypted_value, (SELECT value FROM meta WHERE key='version') FROM cookies WHERE name IN ('operationToken','operToken') AND host_key IN ('agent.teleai.com.cn','.agent.teleai.com.cn') LIMIT 16";
                using var rows = await command.ExecuteReaderAsync(token);
                var operation = ""; var oper = "";
                while (await rows.ReadAsync(token))
                {
                    var name = rows.GetString(0); var value = rows.IsDBNull(1) ? "" : rows.GetString(1); var host = rows.GetString(2);
                    var encrypted = rows.IsDBNull(3) ? [] : (byte[])rows.GetValue(3);
                    if (encrypted.Length > 0)
                    {
                        if (value.Length > 0 || encrypted.Length > 1024 * 1024) return null;
                        var clear = await DecryptWindows(encrypted, DataRoot, token);
                        if (!int.TryParse(rows.GetValue(4).ToString(), out var version) || version <= 0) return null;
                        if (version >= 24)
                        {
                            var expected = SHA256.HashData(Encoding.UTF8.GetBytes(host));
                            if (clear.Length < 32 || !CryptographicOperations.FixedTimeEquals(clear.AsSpan(0, 32), expected)) return null;
                            clear = clear[32..];
                        }
                        value = new UTF8Encoding(false, true).GetString(clear);
                    }
                    if (value.Length > 16384 || value.Any(c => char.IsControl(c) || c == ';')) return null;
                    if (name == "operationToken") { if (operation.Length > 0 && operation != value) return null; operation = value; }
                    else if (name == "operToken") { if (oper.Length > 0 && oper != value) return null; oper = value; }
                }
                if (operation.Length > 0) return new(scope, uid, operation, oper, Hash(scope + "\0" + operation + "\0" + oper + "\0" + Text(credential, "encryptedToken")));
            }
        return null;
    }

    private async Task<JsonElement> Get(Login login, string operation, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://agent.teleai.com.cn/meter/super-agent/user/portal" + operation);
        request.Headers.Add("X-Token", login.Operation); request.Headers.Add("userId", login.UserId);
        request.Headers.Add("Cookie", (login.Oper.Length > 0 ? "operToken=" + login.Oper + "; " : "") + "operationToken=" + login.Operation);
        request.Headers.Referrer = new("https://agent.teleai.com.cn/teleagent-space"); request.Headers.UserAgent.ParseAdd("Mozilla/5.0 TeleAgent"); request.Headers.Accept.Add(new("application/json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is { } finalUri && finalUri != request.RequestUri) throw new HttpRequestException("TeleAgent query failed", null, response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(token); var envelope = await Json(stream, token);
        if (Field(envelope, "code").ToString() is not ("" or "200")) throw new JsonException();
        return Field(envelope, "data");
    }

    internal static QuotaWindow? ParsePoints(JsonElement data, DateTimeOffset observed)
    {
        var balance = Num(data, "pointsBalance");
        if (balance is null && double.TryParse(Text(data, "pointsBalance"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) balance = number;
        if (balance is null) return null;
        DateTimeOffset? expiry = null;
        foreach (var group in Array(data, "groups").Take(64))
            foreach (var active in Array(group, "active").Take(128))
                if (Date(Text(active, "effectiveEnd")) is { } end && (expiry is null || end < expiry)) expiry = end;
        return new("teleagent:points", "积分余额", null, null, null, "积分", expiry, null, "balance", "exact", observed, false, RemainingValue: balance);
    }

    internal static async Task<byte[]> DecryptWindows(byte[] encrypted, string root, CancellationToken token)
    {
        if (encrypted.Length > 1024 * 1024 || encrypted.Length == 0) throw new CryptographicException();
        if (encrypted.AsSpan().StartsWith(new byte[] { 1, 0, 0, 0 })) return ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        if (encrypted.Length < 31 || !encrypted.AsSpan(0, 3).SequenceEqual("v10"u8)) throw new CryptographicException();
        var state = await ReadJson(Path.Combine(root, "Local State"), token);
        var wrapped = Convert.FromBase64String(Text(Field(state, "os_crypt"), "encrypted_key"));
        if (wrapped.Length <= 5 || !wrapped.AsSpan(0, 5).SequenceEqual("DPAPI"u8)) throw new CryptographicException();
        var key = ProtectedData.Unprotect(wrapped[5..], null, DataProtectionScope.CurrentUser);
        try { return DecryptGcm(encrypted, key); } finally { CryptographicOperations.ZeroMemory(key); }
    }
    internal static byte[] DecryptGcm(byte[] encrypted, byte[] key)
    {
        if (key.Length != 32 || encrypted.Length < 31 || encrypted.Length > 1024 * 1024 || !encrypted.AsSpan(0, 3).SequenceEqual("v10"u8)) throw new CryptographicException();
        var clear = new byte[encrypted.Length - 31]; using var aes = new AesGcm(key, 16);
        aes.Decrypt(encrypted.AsSpan(3, 12), encrypted.AsSpan(15, encrypted.Length - 31), encrypted.AsSpan(encrypted.Length - 16), clear); return clear;
    }
}
