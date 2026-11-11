using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WallpaperBridge;

// Read-only port of magpie-community/plugins kiro (MIT). No refresh, login or saveBack.
internal sealed class LocalKiroQuotaReader(HttpClient client, string? userHome = null)
{
    private const string BuilderProfile = "arn:aws:codewhisperer:us-east-1:638616132270:profile/AAAACCCCXXXX";
    private sealed record Login(string Access, string Profile, string Region, string Method, bool Builder, DateTimeOffset? Expires, string Source);
    private string Home => userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public async Task<QuotaData> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var logins = await Logins(token);
            if (logins.Count == 0) return new("unconfigured", null, "Kiro：未发现本地 CLI / IDE 登录", []);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var accounts = new List<QuotaAccount>();
            var failed = false;
            var expired = false;
            foreach (var login in logins.DistinctBy(x => x.Access))
            {
                if (login.Expires <= DateTimeOffset.UtcNow) { expired = true; continue; }
                try
                {
                    var profiles = new Dictionary<string, string>();
                    if (login.Builder) profiles["us-east-1"] = BuilderProfile;
                    else if (login.Profile.Length > 0) profiles[Region(login.Profile, login.Region)] = login.Profile;
                    // Identity Center subscriptions can have independent credits in both regions.
                    if (profiles.Count == 0 || login.Method == "idc" && !login.Builder)
                        foreach (var region in new[] { "us-east-1", "eu-central-1" })
                        {
                            try
                            {
                                var listed = await Request(login, region, "List-Available-Profiles", "{}", deadline.Token);
                                foreach (var profile in Array(listed, "profiles").Take(32))
                                {
                                    var arn = Text(profile, "arn");
                                    if (arn.Length > 0) profiles.TryAdd(Region(arn, region), arn);
                                }
                            }
                            catch (HttpRequestException e) { if (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { expired = true; failed = true; } }
                        }
                    if (profiles.Count == 0) throw new JsonException();
                    var windows = new List<QuotaWindow>();
                    var plan = "";
                    var identity = "";
                    var observed = DateTimeOffset.UtcNow;
                    foreach (var (region, profile) in profiles)
                    {
                        var operation = "Get-Usage-Limits?origin=KIRO_CLI&profileArn=" + Uri.EscapeDataString(profile) + "&resourceType=CREDIT&isEmailRequired=true";
                        try
                        {
                            var data = await Request(login, region, operation, null, deadline.Token);
                            var parsed = ParseUsage(data, observed, region);
                            plan = plan.Length > 0 ? plan : parsed.Plan;
                            identity = identity.Length > 0 ? identity : Text(Field(data, "userInfo"), "email");
                            windows.AddRange(parsed.Windows);
                        }
                        catch (HttpRequestException e) { expired |= e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden; failed = true; }
                    }
                    if (windows.Count == 0) { failed = true; continue; }
                    if (!(await Logins(deadline.Token)).Any(x => x.Source == login.Source && x.Access == login.Access))
                    { failed = true; continue; }
                    if (identity.Length == 0) identity = "token:" + login.Access;
                    var account = new QuotaAccount("local:kiro:" + Hash(login.Method + ":" + identity + ":" + string.Join(',', profiles.OrderBy(p => p.Key).Select(p => p.Value))), "Kiro 本机", "kiro", Safe(plan), windows.ToArray());
                    if (!accounts.Any(x => x.Id == account.Id)) accounts.Add(account);
                }
                catch (HttpRequestException e) { expired |= e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden; failed = true; }
                catch (JsonException) { failed = true; }
            }
            return new(accounts.Count > 0 ? failed || expired ? "stale" : "ok" : expired ? "unauthorized" : "offline",
                accounts.Count > 0 ? DateTimeOffset.UtcNow : null,
                accounts.Count > 0 ? failed || expired ? "Kiro：已读取部分额度，部分登录或区域暂不可用" : "Kiro：已读取本地登录套餐和额度"
                    : expired ? "Kiro：登录已过期，请在原客户端重新登录" : "Kiro：额度查询暂不可用", accounts.ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or SqliteException or HttpRequestException or OperationCanceledException or FormatException or ArgumentException)
        { return new("offline", null, "Kiro：本地登录或额度暂不可读取", []); }
    }

    private async Task<List<Login>> Logins(CancellationToken token)
    {
        var result = new List<Login>();
        var malformed = false;
        var appData = userHome is null ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : Path.Combine(Home, "AppData", "Roaming");
        var path = Path.Combine(appData, "kiro-cli", "data.sqlite3");
        if (File.Exists(path))
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
            try
            {
                await db.OpenAsync(token);
                foreach (var kind in new[] { "social", "odic", "external-idp" })
                {
                    using var command = db.CreateCommand();
                    command.CommandText = "SELECT value FROM auth_kv WHERE key = $key LIMIT 1";
                    command.Parameters.AddWithValue("$key", "kirocli:" + kind + ":token");
                    if (await command.ExecuteScalarAsync(token) is not string raw || raw.Length > 1024 * 1024) continue;
                    using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
                    var m = doc.RootElement;
                    if (m.ValueKind != JsonValueKind.Object) { malformed = true; continue; }
                    if (Text(m, "access_token") is not { Length: > 0 } access) continue;
                    result.Add(new(access, Text(m, "profile_arn"), Text(m, "region"), kind == "odic" ? "idc" : kind,
                        kind == "odic" && Text(m, "start_url") is "" or "https://view.awsapps.com/start", Date(Text(m, "expires_at")), "cli:" + kind));
                    break;
                }
            }
            catch (Exception e) when (e is SqliteException or JsonException) { malformed = true; }
        }
        try
        {
            var m = await ReadJson(Path.Combine(Home, ".aws", "sso", "cache", "kiro-auth-token.json"), token);
            if (m.ValueKind != JsonValueKind.Object) throw new JsonException();
            var access = Text(m, "accessToken");
            if (access.Length > 0) result.Add(new(access, Text(m, "profileArn"), Text(m, "region"),
                Text(m, "authMethod").Equals("social", StringComparison.OrdinalIgnoreCase) || Text(m, "clientIdHash").Length == 0 ? "social" : "idc",
                Text(m, "provider").Equals("builderid", StringComparison.OrdinalIgnoreCase), Date(Text(m, "expiresAt")), "ide"));
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { malformed = true; }
        if (malformed && result.Count == 0) throw new JsonException();
        return result;
    }

    private async Task<JsonElement> Request(Login login, string region, string operation, string? body, CancellationToken token)
    {
        if (region is not ("us-east-1" or "eu-central-1")) throw new JsonException();
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, "https://management." + region + ".kiro.dev/" + operation);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Access);
        if (login.Method == "external-idp") request.Headers.Add("tokentype", "EXTERNAL_IDP");
        request.Headers.Accept.Add(new("application/json"));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is { } finalUri && finalUri != request.RequestUri) throw new HttpRequestException("Kiro query failed", null, response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(token);
        return await Json(stream, token);
    }

    internal static (string Plan, QuotaWindow[] Windows) ParseUsage(JsonElement data, DateTimeOffset observed, string region)
    {
        var windows = new List<QuotaWindow>();
        var overage = Field(data, "overageConfiguration");
        var overageOn = Text(overage, "overageStatus").Equals("ENABLED", StringComparison.OrdinalIgnoreCase) || Field(overage, "overageEnabled").ValueKind == JsonValueKind.True;
        var i = 0;
        foreach (var u in Array(data, "usageBreakdownList").Take(32))
        {
            var prefix = "kiro:" + region + ":" + i++;
            var trial = Field(u, "freeTrialInfo");
            if (Text(trial, "freeTrialStatus").Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) && Num(trial, "usageLimitWithPrecision") is > 0)
                windows.Add(Window(prefix + ":trial", "免费试用 · " + region, Num(trial, "currentUsageWithPrecision"), Num(trial, "usageLimitWithPrecision"), Stamp(Num(trial, "freeTrialExpiry")), null, observed));
            var used = Num(u, "currentUsageWithPrecision");
            var limit = Num(u, "usageLimitWithPrecision");
            var reset = Stamp(Num(u, "nextDateReset") ?? Num(data, "nextDateReset"));
            var cap = overageOn ? Num(u, "overageCapWithPrecision") is > 0 and var precise ? precise : Num(u, "overageCap") : null;
            if (limit is > 0) windows.Add(Window(prefix + ":plan", "套餐 Credits · " + region, cap is > 0 && used.HasValue ? Math.Min(used.Value, limit.Value) : used, limit, reset, 2592000, observed));
            if (cap is > 0) windows.Add(Window(prefix + ":overage", "超额 Credits · " + region, Num(u, "currentOveragesWithPrecision") ?? Num(u, "currentOverages") ?? (used.HasValue && limit.HasValue ? Math.Max(0, used.Value - limit.Value) : null), cap, reset, 2592000, observed));
            var bonusIndex = 0;
            foreach (var bonus in Array(u, "bonuses").Take(32))
                if (Text(bonus, "status").Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) && Num(bonus, "usageLimit") is > 0
                    && (Stamp(Num(bonus, "expiresAt")) is not { } expiry || expiry > observed))
                    windows.Add(Window(prefix + ":bonus:" + bonusIndex++, "奖励 Credits · " + region, Num(bonus, "currentUsage"), Num(bonus, "usageLimit"), Stamp(Num(bonus, "expiresAt")), null, observed));
        }
        return (Text(Field(data, "subscriptionInfo"), "subscriptionTitle"), windows.ToArray());
    }

    private static string Region(string profile, string fallback) => profile.Split(':') is { Length: > 4 } parts && parts[3] is "us-east-1" or "eu-central-1" ? parts[3] : fallback.StartsWith("eu-", StringComparison.Ordinal) ? "eu-central-1" : "us-east-1";
    internal static JsonElement Field(JsonElement m, string n) => m.ValueKind == JsonValueKind.Object && m.TryGetProperty(n, out var v) ? v : default;
    internal static string Text(JsonElement m, string n) => Field(m, n) is { ValueKind: JsonValueKind.String } v ? v.GetString()!.Trim() : "";
    internal static double? Num(JsonElement m, string n) => Field(m, n) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
    internal static IEnumerable<JsonElement> Array(JsonElement m, string n) => Field(m, n) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : [];
    internal static DateTimeOffset? Date(string s) => DateTimeOffset.TryParse(s, out var d) ? d : null;
    internal static DateTimeOffset? Stamp(double? n, bool millis = false) { try { return n is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(millis ? n.Value : n.Value * 1000))) : null; } catch (ArgumentOutOfRangeException) { return null; } catch (OverflowException) { return null; } }
    internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    internal static bool TokenExpired(string token)
    {
        try
        {
            var parts = token.Split('.'); if (parts.Length != 3 || parts[1].Length > 65536) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/'); payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload), new JsonDocumentOptions { MaxDepth = 32 });
            return Num(doc.RootElement, "exp") is { } expiry && expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch (Exception e) when (e is FormatException or JsonException) { return false; }
    }
    internal static string Safe(string s) => s.Length <= 100 && !s.Contains('@') && !s.Contains("://", StringComparison.Ordinal)
        && !s.Contains("Bearer", StringComparison.OrdinalIgnoreCase) && !s.Any(char.IsControl)
        && !System.Text.RegularExpressions.Regex.IsMatch(s, @"(?i)(?:\b(?:sk|pk|ghp|gho|ghu|github_pat)[-_][A-Za-z0-9_-]{8,}|eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+|[A-Za-z0-9_-]{48,})") ? s : "";
    internal static QuotaWindow Window(string id, string label, double? used, double? limit, DateTimeOffset? reset, long? span, DateTimeOffset observed, double? percent = null, string unit = "credits") => new(id, label,
        limit is > 0 && used.HasValue ? Math.Clamp(100 * (1 - used.Value / limit.Value), 0, 100) : percent.HasValue ? Math.Clamp(100 - percent.Value, 0, 100) : null,
        used, limit, unit, reset, span, "used", "exact", observed, false);
    internal static async Task<JsonElement> ReadJson(string path, CancellationToken token) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous); return await Json(stream, token); }
    internal static async Task<JsonElement> Json(Stream stream, CancellationToken token)
    {
        using var memory = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0) { if (memory.Length + read > 1024 * 1024) throw new JsonException(); memory.Write(buffer, 0, read); }
        using var doc = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        return doc.RootElement.Clone();
    }
}
