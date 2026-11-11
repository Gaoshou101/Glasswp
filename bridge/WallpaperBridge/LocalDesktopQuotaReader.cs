using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WallpaperBridge;

// Native Windows login readers. Usage protocols and Trae's local codec follow
// magpie-community/plugins (MIT); see docs/AI-SOURCES-2026-11-10.md for attribution.
// Never refresh a token, start a login, invoke an agent, or write its credential store.
internal sealed class LocalDesktopQuotaReader(HttpClient client, string? userHome = null)
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private sealed record Result(string Status, string Message, QuotaAccount? Account = null);
    private sealed class UnsupportedLogin : Exception;
    private sealed class LoginChanged : Exception;
    private string Home => userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string Roaming => userHome is null ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : Path.Combine(Home, "AppData", "Roaming");
    private string Local => userHome is null ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : Path.Combine(Home, "AppData", "Local");
    private static JsonElement Field(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string Text(JsonElement root, string name) => Field(root, name).ValueKind == JsonValueKind.String ? Field(root, name).GetString()!.Trim() : "";
    private static string Identity(JsonElement root, string name) => Field(root, name) is { ValueKind: JsonValueKind.Number } value ? value.GetRawText() : Text(root, name);
    private static JsonElement[] Array(JsonElement root, string name) => Field(root, name).ValueKind == JsonValueKind.Array ? Field(root, name).EnumerateArray().ToArray() : [];
    private static double? Number(JsonElement root, string name)
    {
        var field = Field(root, name);
        return field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var value) && double.IsFinite(value) ? value
            : field.ValueKind == JsonValueKind.String && double.TryParse(field.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) ? value : null;
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string CursorPlan(string value, string access, string identity)
    {
        if (value.Length > 80 || value.Any(char.IsControl) || value.Contains('@') || value.Contains("://", StringComparison.Ordinal) || value.Contains("www.", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(value, @"(?:eyJ[A-Za-z0-9_-]*\.|\b(?:sk-|ghp_|gho_|github_pat_))", RegexOptions.CultureInvariant) ||
            new[] { access, identity }.Any(secret => secret.Length > 0 && value.Contains(secret, StringComparison.Ordinal))) return "";
        return value.ToLowerInvariant() switch {
            "free" => "Free", "hobby" => "Hobby", "pro" => "Pro", "pro+" or "pro plus" => "Pro+",
            "ultra" => "Ultra", "team" or "teams" => "Teams", "business" => "Business",
            "enterprise" => "Enterprise", "student" => "Student", "education" => "Education", _ => value
        };
    }
    private static QuotaAccount Account(string provider, string alias, string identity, string plan = "", QuotaWindow[]? windows = null) => new("local:" + provider + ":" + Hash(identity), alias, provider, plan, windows ?? []);
    private static JsonElement Claims(string jwt)
    {
        try {
            var parts = jwt.Split('.'); if (parts.Length != 3 || parts[1].Length > 32768) return default;
            var body = parts[1].Replace('-', '+').Replace('_', '/');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(body.PadRight(body.Length + (4 - body.Length % 4) % 4, '=')));
            return doc.RootElement.Clone();
        } catch (Exception e) when (e is JsonException or FormatException) { return default; }
    }
    private static bool Expired(string jwt) => Number(Claims(jwt), "exp") is { } exp && exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static DateTimeOffset? Time(JsonElement root, string name)
    {
        try {
            if (Number(root, name) is > 0 and var value) return DateTimeOffset.FromUnixTimeMilliseconds((long)(value < 1e11 ? value * 1000 : value));
            return DateTimeOffset.TryParse(Text(root, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        } catch (ArgumentOutOfRangeException) { return null; }
    }
    private static QuotaWindow Window(string id, string label, double? used, double? limit, DateTimeOffset? reset = null, string unit = "credits", double? usedPercent = null, long? duration = null) => new(
        id, label, usedPercent is { } percent ? Math.Clamp(100 - percent, 0, 100) : used is >= 0 && limit is > 0 ? Math.Clamp(100 - 100 * used.Value / limit.Value, 0, 100) : null,
        used, limit, unit, reset, duration, "fixed", reset is null ? "unknown" : "exact", DateTimeOffset.UtcNow, false);

    public async Task<QuotaData> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var tasks = new List<Task<Result>> {
            Capture("Cursor", () => Cursor(token), token),
            Capture("WorkBuddy", () => WorkBuddy(false, token), token),
            Capture("WorkBuddy AI", () => WorkBuddy(true, token), token)
        };
        foreach (var profile in new[] { "Trae CN", "TRAE SOLO CN", "trae-cn" }) {
            var name = profile; tasks.Add(Capture(name, () => Trae(name, token), token));
        }
        var results = await Task.WhenAll(tasks);
        var present = results.Where(r => r.Status != "missing").ToArray();
        var accounts = present.Where(r => r.Account is not null).Select(r => r.Account!).GroupBy(a => a.Id).Select(g => g.First()).ToArray();
        var success = present.Any(r => r.Status == "ok");
        var status = present.Length == 0 ? "unconfigured" : present.All(r => r.Status == "ok") ? "ok" : success ? "stale"
            : present.Any(r => r.Status == "unauthorized") ? "unauthorized" : present.Any(r => r.Status == "unsupported") ? "unsupported" : "offline";
        return new(status, success ? DateTimeOffset.UtcNow : null, present.Length == 0 ? "未检测到其他本机 AI agent 登录" : string.Join("；", present.Select(r => r.Message)), accounts);
    }
    private static async Task<Result> Capture(string alias, Func<Task<Result>> read, CancellationToken token)
    {
        try { return await read(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (FileNotFoundException) { return new("missing", ""); }
        catch (DirectoryNotFoundException) { return new("missing", ""); }
        catch (UnsupportedLogin) { return new("unsupported", alias + "：原工具使用加密登录存储，暂不能直接读取，可使用 Magpie 来源"); }
        catch (LoginChanged) { return new("unconfigured", alias + "：登录已切换，等待重新读取"); }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { return new("unauthorized", alias + "：登录已失效或权限不足，请回原工具检查登录"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or OperationCanceledException or CryptographicException or FormatException or ArgumentException or OverflowException) { return new("offline", alias + "：本机登录或额度暂不可读取"); }
    }
    private static async Task<JsonElement> Parse(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk, token)) > 0) { if (buffer.Length + count > MaximumBytes) throw new JsonException(); buffer.Write(chunk, 0, count); }
        using var doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 40 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        return doc.RootElement.Clone();
    }
    private static async Task<JsonElement> FileJson(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
        if (stream.Length > MaximumBytes) throw new JsonException();
        return await Parse(stream, token);
    }
    private async Task<JsonElement> Request(string url, string access, object? body, Dictionary<string, string>? headers, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access); req.Headers.Accept.ParseAdd("application/json");
        if (body is not null) req.Content = JsonContent.Create(body);
        if (headers is not null) foreach (var header in headers) { req.Headers.Remove(header.Key); req.Headers.TryAddWithoutValidation(header.Key, header.Value); }
        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != req.RequestUri) throw new HttpRequestException();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new JsonException();
        using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
        return await Parse(stream, budget.Token);
    }

    private async Task<Result> Cursor(CancellationToken token)
    {
        var path = Path.Combine(Roaming, "Cursor", "auth.json");
        var login = await FileJson(path, token); var access = Text(login, "accessToken");
        if (access.Length == 0) return new("missing", "");
        if (Expired(access)) return new("unauthorized", "Cursor：登录已过期，请回原工具登录");
        var identity = Text(Claims(access), "sub"); if (identity.Length == 0) identity = access;
        var headers = new Dictionary<string, string> { ["Connect-Protocol-Version"] = "1" };
        try {
            var configDir = userHome is null ? Environment.GetEnvironmentVariable("CURSOR_CONFIG_DIR") : null;
            if (string.IsNullOrWhiteSpace(configDir)) configDir = Path.Combine(Home, ".cursor");
            var cfg = Field(await FileJson(Path.Combine(configDir, "cli-config.json"), token), "authInfo");
            if (Text(cfg, "authId") == identity && Number(cfg, "activeTeamId") is > 0 and var team && team == Math.Truncate(team)) {
                headers["x-cursor-team-id"] = team.ToString(CultureInfo.InvariantCulture); identity += ":team:" + team;
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        JsonElement plan = default;
        try { plan = await Request("https://api2.cursor.sh/aiserver.v1.DashboardService/GetPlanInfo", access, new { }, headers, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException) { }
        var usage = await Request("https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage", access, new { }, headers, token);
        if (Text(await FileJson(path, token), "accessToken") != access) throw new LoginChanged();
        var values = Field(usage, "planUsage"); var reset = Time(usage, "billingCycleEnd");
        var windows = new List<QuotaWindow>();
        if (Field(plan, "isMerged").ValueKind == JsonValueKind.True) {
            var period = Text(Field(plan, "planInfo"), "includedUsagePeriod");
            var weekly = period == "INCLUDED_USAGE_PERIOD_WEEKLY" || Number(Field(plan, "planInfo"), "includedUsagePeriod") == 2;
            if (Number(values, "totalPercentUsed") is { } percent) windows.Add(Window("included", weekly ? "周额度" : "套餐额度", null, null, reset, "%", percent, weekly ? 604800 : null));
        } else foreach (var (key, label, id) in new[] { ("autoPercentUsed", "Cursor 模型", "cursor-models"), ("apiPercentUsed", "其他模型", "other-models"), ("totalPercentUsed", "总额度", "total") })
            if (Number(values, key) is { } percent) windows.Add(Window(id, label, null, null, reset, "%", percent));
        var planName = CursorPlan(Text(Field(plan, "planInfo"), "planName"), access, Text(Claims(access), "sub"));
        if (windows.Count == 0 && planName.Length == 0) throw new JsonException();
        return new("ok", windows.Count > 0 ? "Cursor：已读取本机套餐与额度" : "Cursor：已读取套餐，此账户未提供可量化额度", Account("cursor", "Cursor 本机", identity, planName, windows.ToArray()));
    }

    private async Task<Result> WorkBuddy(bool international, CancellationToken token)
    {
        var provider = international ? "workbuddy-ai" : "workbuddy"; var alias = international ? "WorkBuddy AI" : "WorkBuddy";
        var host = international ? "https://www.workbuddy.ai" : "https://copilot.tencent.com";
        var path = Path.Combine(Local, "CodeBuddyExtension", "Data", "Public", "auth", international ? "workbuddy-desktop-ai.info" : "workbuddy-desktop.info");
        var login = await FileJson(path, token); var auth = Field(login, "auth"); var member = Field(login, "account");
        if (Field(auth, "accessToken").ValueKind == JsonValueKind.Object) throw new UnsupportedLogin();
        var access = Text(auth, "accessToken"); var uid = Identity(member, "uid");
        if (access.Length == 0 || uid.Length == 0) return new("missing", "");
        if (Expired(access) || Time(auth, "expiresAt") is { } expires && expires <= DateTimeOffset.UtcNow) return new("unauthorized", alias + "：登录已过期，请回原工具登录");
        var headers = new Dictionary<string, string> { ["User-Agent"] = "WorkBuddy/5.9.3", ["X-User-Id"] = uid,
            ["X-Domain"] = Text(auth, "domain") is { Length: > 0 } domain ? domain : new Uri(host).Host, ["X-Product"] = "SaaS", ["X-IDE-Type"] = "WorkBuddy" };
        var enterprise = Identity(member, "enterpriseId");
        if (enterprise.Length > 0) { headers["X-Enterprise-Id"] = enterprise; headers["X-Tenant-Id"] = enterprise; }
        var root = await Request(host + "/billing/meter/get-user-resource-summary", access, new { }, headers, token);
        if (Number(root, "code") is not (null or 0)) throw new JsonException();
        var sum = Field(root, "data");
        if (sum.ValueKind != JsonValueKind.Object || Field(sum, "Packages").ValueKind != JsonValueKind.Array && Field(sum, "CreditGroups").ValueKind != JsonValueKind.Array && Field(sum, "IsPaidUser").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
        var plan = enterprise.Length > 0 ? "Enterprise" : Field(sum, "IsPaidUser").ValueKind == JsonValueKind.True ? "Pro" : Field(sum, "IsPaidUser").ValueKind == JsonValueKind.False ? "Free" : "";
        var groups = Array(sum, "CreditGroups").Where(g => Number(g, "CycleTotalCapacity") is >= 0).ToArray(); var packages = groups.Length > 0 ? groups : Array(sum, "Packages");
        double used = 0, total = 0; var valid = false;
        foreach (var package in packages) if (Number(package, "CycleTotalCapacity") is >= 0 and var limit && Number(package, "CycleUsedCapacity") is >= 0 and var spent) { total += limit; used += spent; valid = true; }
        if (!double.IsFinite(total) || !double.IsFinite(used)) throw new JsonException();
        var windows = new List<QuotaWindow>();
        if (valid && total > 0) windows.Add(Window("credits", "积分额度", used, total));
        if (windows.Count == 0 && enterprise.Length > 0) {
            var entRoot = await Request(host + "/billing/meter/get-enterprise-user-usage", access, new { }, headers, token);
            if (Number(entRoot, "code") is not (null or 0)) throw new JsonException();
            var ent = Field(entRoot, "data");
            if (Number(ent, "limitNum") is > 0 and var credit && Number(ent, "credit") is >= 0 and var spent) windows.Add(Window("enterprise-credits", "团队积分额度", spent, credit));
        }
        var current = await FileJson(path, token);
        if (Text(Field(current, "auth"), "accessToken") != access || Identity(Field(current, "account"), "uid") != uid || Identity(Field(current, "account"), "enterpriseId") != enterprise) throw new LoginChanged();
        return new("ok", alias + (windows.Count > 0 ? "：已读取本机套餐与额度" : "：登录有效，此账户未提供可量化额度"), Account(provider, alias + " 本机", uid + ":" + enterprise, plan, windows.ToArray()));
    }

    private async Task<Result> Trae(string profile, CancellationToken token)
    {
        var path = Path.Combine(Roaming, profile, "User", "globalStorage", "storage.json");
        var root = await FileJson(path, token); var login = TraeLogin(root);
        var access = Text(login, "token"); var uid = Identity(login, "userId");
        if (access.Length == 0 || uid.Length == 0) return new("missing", "");
        if (Expired(access)) return new("unauthorized", profile + "：登录已过期，请回原工具登录");
        var headers = new Dictionary<string, string> { ["Authorization"] = "Cloud-IDE-JWT " + access, ["X-Cloudide-Token"] = access,
            ["x-ide-token"] = access, ["x-uid"] = uid, ["x-app-id"] = "6eefa01c-1036-4c7e-9ca5-d891f63bfcd8", ["x-app-version"] = "0.1.69",
            ["x-app-version-code"] = "20260917", ["x-ide-version"] = "0.1.69", ["x-ide-version-code"] = "20260917", ["x-ide-version-type"] = "stable",
            ["x-request-id"] = Guid.NewGuid().ToString(), ["x-device-type"] = "windows", ["x-system-type"] = "Windows" };
        var deviceIds = root.EnumerateObject().Where(p => p.Name.StartsWith("iCubeAuthInfo://icube-dc:", StringComparison.Ordinal)).Select(p => p.Name["iCubeAuthInfo://icube-dc:".Length..]).ToArray();
        if (deviceIds.Length == 1 && deviceIds[0].Length is >= 8 and <= 20 && deviceIds[0].All(char.IsAsciiDigit)) headers["x-device-id"] = deviceIds[0];
        if (Text(root, "telemetry.machineId") is { Length: > 0 } machine) headers["x-machine-id"] = machine;
        var usage = await Request("https://api.trae.cn/trae/api/v2/pay/ide_user_ent_usage", access, new { require_usage = true, req_source = 0 }, headers, token);
        if (Number(usage, "code") is not (null or 0)) throw new JsonException();
        // Trae's usage response puts entitlement fields at the top level.
        if (Field(usage, "user_entitlement_pack_list").ValueKind != JsonValueKind.Array && Field(usage, "data").ValueKind == JsonValueKind.Object) usage = Field(usage, "data");
        if (Field(usage, "user_entitlement_pack_list").ValueKind != JsonValueKind.Array) throw new JsonException();
        double total = 0, used = 0; bool unlimited = false, valid = false; DateTimeOffset? reset = null;
        foreach (var pack in Array(usage, "user_entitlement_pack_list")) {
            var info = Field(pack, "entitlement_base_info"); var quota = Field(info, "quota");
            if (Number(quota, "credits_limit") is not { } limit) continue;
            if (limit == -1) unlimited = true; else if (limit >= 0) { total += limit; valid = true; }
            if (Number(Field(pack, "usage"), "credits_amount") is >= 0 and var spent) used += spent;
            var end = Time(info, "end_time") ?? Time(info, "expire_time") ?? Time(pack, "end_time"); if (end is not null && (reset is null || end < reset)) reset = end;
        }
        if (!double.IsFinite(total) || !double.IsFinite(used)) throw new JsonException();
        var current = TraeLogin(await FileJson(path, token));
        if (Text(current, "token") != access || Identity(current, "userId") != uid) throw new LoginChanged();
        var windows = unlimited ? new[] { Window("credits", "积分额度", used, null, reset) with { Mode = "non_window", DisplayText = "不限量" } }
            : valid && total > 0 ? new[] { Window("credits", "积分额度", used, total, reset) } : System.Array.Empty<QuotaWindow>();
        var plan = unlimited ? "Unlimited" : Field(usage, "is_credits_billing").ValueKind == JsonValueKind.True ? "Credits" : "Free";
        return new("ok", profile + "：已读取本机套餐与额度", Account("trae-cn", profile + " 本机", uid, plan, windows));
    }
    private static JsonElement TraeLogin(JsonElement root)
    {
        var value = Text(root, "iCubeAuthInfo://icube.cloudide"); if (value.Length == 0) return default;
        if (!value.StartsWith('{')) value = DecodeTrae(value);
        using var doc = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 24 }); return doc.RootElement.Clone();
    }
    private static readonly Dictionary<string, byte[]> TraeSalts = new() {
        ["746305100000"] = Xor([82,9,106,213,48,54,165,56,191,64,163,158,129,243,215,251,124,227,57,130,155,47,255,135,52,142,67,68,196,222,233,203,84,123,148,50,166,194,35,61,238,76,149,11,66,250,195,78,8,46,161,102,40,217,36,178,118,91,162,73,109,139,209,37], [31,221,168,51,136,7,199,49,177,18,16,89,39,128,236,95,96,81,127,169,25,181,74,13,45,229,122,159,147,201,156,239,160,224,59,77,174,42,245,176,200,235,187,60,131,83,153,97,23,43,4,126,186,119,214,38,225,105,20,99,85,33,12,125]),
        ["123920200203"] = Xor([191,192,216,250,122,246,220,97,31,254,98,27,8,72,71,176,135,99,96,18,127,101,203,104,211,102,191,125,37,72,150,156,51,229,121,35,17,153,141,177,110,131,150,128,172,255,254,6,18,140,55,62,236,249,135,64,135,12,117,4,89,149,168,209], [246,204,26,232,232,70,129,109,223,146,169,242,23,241,105,145,50,196,165,42,254,120,3,54,244,207,209,85,53,6,138,106,175,148,31,204,186,186,165,182,87,142,49,10,39,110,26,154,86,56,173,125,18,64,198,225,99,99,83,82,191,134,76,170])
    };
    private static byte[] Xor(byte[] a, byte[] b) => a.Select((v, i) => (byte)(v ^ b[i])).ToArray();
    internal static string DecodeTrae(string value)
    {
        var bytes = Convert.FromBase64String(value); if (bytes.Length <= 102 || bytes.Length > MaximumBytes) throw new CryptographicException();
        if (!TraeSalts.TryGetValue(Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant(), out var salt)) throw new CryptographicException();
        var key = SHA512.HashData(SHA512.HashData(bytes.AsSpan(6, 32)).Concat(salt).ToArray());
        using var aes = Aes.Create(); aes.Key = key[..16]; aes.IV = key[16..32];
        var plaintext = aes.DecryptCbc(bytes.AsSpan(38), aes.IV);
        if (plaintext.Length < 64 || !CryptographicOperations.FixedTimeEquals(SHA512.HashData(plaintext.AsSpan(64)), plaintext.AsSpan(0, 64))) throw new CryptographicException();
        return Encoding.UTF8.GetString(plaintext.AsSpan(64));
    }
}
