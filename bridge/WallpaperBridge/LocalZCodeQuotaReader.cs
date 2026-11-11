using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static WallpaperBridge.LocalKiroQuotaReader;

namespace WallpaperBridge;

// Read-only port of magpie-community/plugins zcode (MIT): local decrypt and quota GETs only.
internal sealed class LocalZCodeQuotaReader(HttpClient client, string? userHome = null)
{
    private sealed record Login(string Key, string Jwt, string Token, string Site, string Org, string Project, string KeyId, bool Team, string Identity, string Fingerprint);
    private string Home => userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string Settings => Path.Combine(Home, ".zcode", "v2");

    public async Task<QuotaData> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var login = await ReadLogin(token);
            if (login is null) return new("unconfigured", null, "ZCode：未发现本地登录", []);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var windows = new List<QuotaWindow>();
            var plan = "";
            var partial = false;
            var unauthorized = false;
            var key = login.Key;
            var root = login.Site == "bigmodel" ? login.Team ? "https://bigmodel.cn" : "https://open.bigmodel.cn" : "https://api.z.ai";
            if (key.Length == 0 && login.Token.Length > 0 && login.Org.Length > 0 && login.Project.Length > 0)
            {
                try { key = await ExistingProjectKey(login, timeout.Token); }
                catch (HttpRequestException e) { partial = true; unauthorized = e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden; }
                catch (JsonException) { partial = true; }
            }
            var observed = DateTimeOffset.UtcNow;
            if (key.Length > 0)
            {
                try
                {
                    var usage = await Get(root + "/api/monitor/usage/quota/limit" + (login.Team ? "?type=2" : ""), key, login, timeout.Token);
                    windows.AddRange(ParseLimits(usage, observed));
                    var level = Safe(Text(usage, "level"));
                    plan = level.Length > 0 ? "GLM Coding " + level : login.Team ? "GLM Coding Team" : "GLM Coding Plan";
                    if (login.Team && login.Token.Length > 0)
                    {
                        try
                        {
                            var detail = await Get(root + "/api/biz/team/subscribe/product/querySubscribeDetail", login.Token, login, timeout.Token);
                            if (Text(detail, "status") == "EFFECTIVE" && Text(detail, "memberGrantStatus") == "VALID") plan = Safe(Text(detail, "productName")) is { Length: > 0 } name ? name : plan;
                        }
                        catch (Exception e) when (e is HttpRequestException or JsonException) { partial = true; }
                    }
                }
                catch (HttpRequestException e) { partial = true; unauthorized |= e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden; }
                catch (JsonException) { partial = true; }
            }
            if (login.Jwt.Length > 0)
            {
                if (TokenExpired(login.Jwt)) { partial = true; unauthorized = true; }
                else try
                {
                    var balance = await Get("https://zcode.z.ai/api/v1/zcode-plan/billing/balance?app_version=3.17.2", "Bearer " + login.Jwt, login, timeout.Token);
                    var gift = ParseBalance(balance, observed);
                    windows.AddRange(gift.Windows);
                    if (plan.Length == 0) plan = gift.Plan;
                }
                catch (HttpRequestException e) { partial = true; unauthorized |= e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden; }
                catch (JsonException) { partial = true; }
            }
            var after = await ReadLogin(timeout.Token);
            if (after?.Fingerprint != login.Fingerprint) return new("unconfigured", null, "ZCode：登录已切换，等待重新读取", []);
            if (windows.Count == 0) return new(unauthorized ? "unauthorized" : "offline", null,
                unauthorized ? "ZCode：登录不可用，请在原客户端重新登录" : "ZCode：未能读取额度；团队套餐需要客户端已有的项目密钥", []);
            return new(partial ? "stale" : "ok", observed, partial ? "ZCode：已读取部分额度，部分套餐暂不可用" : "ZCode：已读取本地登录套餐和额度",
                [new("local:zcode:" + Hash(login.Identity), "ZCode 本机", "zcode", Safe(plan), windows.ToArray())]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException or HttpRequestException or OperationCanceledException or ArgumentException or FormatException)
        { return new("offline", null, "ZCode：本地登录或额度暂不可读取", []); }
    }

    private async Task<Login?> ReadLogin(CancellationToken token)
    {
        var settings = await Optional(Path.Combine(Settings, "setting.json"), token);
        var moved = Text(settings, "dataBaseDir");
        var directory = Path.Combine(Path.IsPathFullyQualified(moved) ? moved : Home, ".zcode", "v2");
        var store = await Optional(Path.Combine(directory, "credentials.json"), token);
        if (store.ValueKind == JsonValueKind.Undefined) return null;
        if (store.ValueKind != JsonValueKind.Object) throw new JsonException();
        var secret = userHome is null ? Environment.GetEnvironmentVariable("ZCODE_CREDENTIAL_SECRET") : null;
        secret = string.IsNullOrEmpty(secret) ? "zcode-credential-fallback:win32:" + Home + ":" + Environment.UserName.Split('\\').Last() : secret;
        var cipher = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        string Dec(string name) => Decrypt(cipher, Text(store, name));
        var site = Text(settings, "providerFamilyDomain") == "bigmodel" ? "bigmodel" : "zai";
        var key = "";
        foreach (var field in store.EnumerateObject())
            if (field.Name.Contains(":coding-plan:", StringComparison.Ordinal) && field.Name.EndsWith(":api-key", StringComparison.Ordinal) && field.Value.ValueKind == JsonValueKind.String)
            {
                var candidate = Decrypt(cipher, field.Value.GetString()!);
                var family = field.Name.Contains(":bigmodel-", StringComparison.Ordinal) ? "bigmodel" : "zai";
                if (candidate.Contains('.') && (key.Length == 0 || family == "zai" && site != "zai")) { key = candidate; site = family; }
            }
        if (key.Length == 0)
        {
            var config = await Optional(Path.Combine(directory, "config.json"), token);
            var providers = Field(config, "provider");
            foreach (var family in new[] { "zai", "bigmodel" })
                foreach (var id in new[] { "builtin:" + family + "-coding-plan", "account:" + family + "-individual-coding-plan" })
                {
                    var k = Text(Field(Field(providers, id), "options"), "apiKey");
                    if (k.StartsWith("enc:", StringComparison.Ordinal)) k = Decrypt(cipher, k);
                    if (key.Length == 0 && k.Contains('.') && !k.Any(char.IsWhiteSpace) && !k.Contains('{') && !k.Contains('$')) { key = k; site = family; }
                }
        }
        var jwt = Dec("zcodejwttoken");
        if (jwt.StartsWith("Bearer ", StringComparison.Ordinal)) jwt = jwt[7..];
        var org = ""; var project = ""; var keyId = ""; var oauth = ""; var team = false;
        foreach (var family in Text(settings, "providerFamilyDomain") is "zai" or "bigmodel" ? new[] { Text(settings, "providerFamilyDomain") } : new[] { "zai", "bigmodel" })
        {
            var sel = Field(Field(settings, "providerFamilyConnectionSelections"), family);
            var o = Text(sel, "organizationId"); var p = Text(sel, "projectId");
            if (sel.ValueKind == JsonValueKind.Undefined)
            {
                var legacy = Text(Field(settings, "modelProviderFamilySelectedKeys"), family);
                var prefix = "team-plan:builtin:" + family + "-coding-plan:";
                if (legacy.StartsWith(prefix, StringComparison.Ordinal) && legacy[prefix.Length..].Split(':') is { Length: 3 } fields)
                    try { o = Uri.UnescapeDataString(fields[1]); p = Uri.UnescapeDataString(fields[2]); } catch (UriFormatException) { }
            }
            var tok = Dec("oauth:" + family + ":access_token");
            if ((Text(sel, "kind") == "team-coding-plan" || sel.ValueKind == JsonValueKind.Undefined) && o.Length > 0 && p.Length > 0 && tok.Length > 0 && !(family == "bigmodel" && tok == jwt))
            { org = o; project = p; oauth = tok; site = family; key = ""; team = true; break; }
        }
        // ZCode 3.16 stores an existing key's location instead of its secret. Read that key only.
        if (key.Length == 0 && org.Length == 0)
            foreach (var family in new[] { site, site == "zai" ? "bigmodel" : "zai" })
            {
                var tok = Dec("oauth:" + family + ":access_token");
                if (tok.Length == 0) continue;
                var user = Parse(Dec("oauth:" + family + ":user_info"));
                var uid = Identity(user);
                foreach (var field in store.EnumerateObject())
                {
                    const string prefix = "project-key-location:v1:";
                    if (!field.Name.StartsWith(prefix, StringComparison.Ordinal) || field.Value.ValueKind != JsonValueKind.String) continue;
                    JsonElement at;
                    try { at = Parse(Uri.UnescapeDataString(field.Name[prefix.Length..])); } catch (UriFormatException) { continue; }
                    if (at.ValueKind != JsonValueKind.Array || at.GetArrayLength() < 5 || at[1].ValueKind != JsonValueKind.String || at[1].GetString() != family || at[3].ValueKind != JsonValueKind.Null || at[4].ToString() != "1") continue;
                    if (uid.Length > 0 && at[2].ToString() != uid) continue;
                    var location = Parse(Decrypt(cipher, field.Value.GetString()!));
                    var o = Text(location, "organizationId"); var p = Text(location, "projectId"); var id = Text(location, "apiKeyId");
                    if (o.Length > 0 && p.Length > 0 && id.Length > 0) { org = o; project = p; keyId = id; oauth = tok; site = family; break; }
                }
                if (keyId.Length > 0) break;
            }
        if (key.Length == 0 && jwt.Length == 0 && oauth.Length == 0) return null;
        var info = Parse(Dec("oauth:" + site + ":user_info"));
        var identity = Identity(info);
        if (identity.Length == 0) identity = Text(info, "email");
        if (identity.Length == 0) identity = "credential:" + Hash(key + jwt + oauth);
        return new(key, jwt, oauth, site, org, project, keyId, team, site + ":" + identity + ":" + (team ? org + ":" + project : ""),
            Hash(string.Join('\0', key, jwt, oauth, site, org, project, keyId, team.ToString())));
    }

    private async Task<string> ExistingProjectKey(Login login, CancellationToken token)
    {
        var root = login.Site == "bigmodel" ? "https://bigmodel.cn" : "https://api.z.ai";
        var url = root + "/api/biz/v1/organization/" + Uri.EscapeDataString(login.Org) + "/projects/" + Uri.EscapeDataString(login.Project) + "/api_keys";
        var authorization = login.Site == "bigmodel" ? login.Token : "Bearer " + login.Token;
        var id = login.KeyId;
        if (id.Length == 0)
        {
            var keys = await Get(url, authorization, login, token);
            if (keys.ValueKind == JsonValueKind.Array)
                foreach (var k in keys.EnumerateArray().Take(128))
                    if (Text(k, "name") == "zcode-team-api-key" && Field(k, "keyType").ToString() == "2") id = Text(k, "apiKey");
        }
        if (id.Length == 0) return ""; // Never create a key on behalf of the user.
        var copied = await Get(url + "/copy/" + Uri.EscapeDataString(id), authorization, login, token);
        var secret = Text(copied, "secretKey");
        return secret.Length > 0 ? id + "." + secret : login.KeyId.Length == 0 ? id : "";
    }

    private async Task<JsonElement> Get(string url, string authorization, Login login, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.Accept.Add(new("application/json"));
        request.Headers.UserAgent.ParseAdd("ZCode/3.17.2");
        if (request.RequestUri!.Host == "zcode.z.ai") request.Headers.Add("X-Device-Mid", Hash(Environment.MachineName)[..32]);
        if (login.Team && !url.StartsWith("https://zcode.z.ai/", StringComparison.Ordinal))
        { request.Headers.Add("Bigmodel-Organization", login.Org); request.Headers.Add("Bigmodel-Project", login.Project); request.Headers.Add("Set-Language", login.Site == "bigmodel" ? "zh" : "en"); }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is { } finalUri && finalUri != request.RequestUri) throw new HttpRequestException("ZCode query failed", null, response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(token);
        var envelope = await Json(stream, token);
        var code = Field(envelope, "code").ToString();
        if (code is "401" or "403" or "1000") throw new HttpRequestException("ZCode login rejected", null, HttpStatusCode.Unauthorized);
        if (code is not ("" or "0" or "200")) throw new JsonException();
        return Field(envelope, "data");
    }

    internal static QuotaWindow[] ParseLimits(JsonElement data, DateTimeOffset observed)
    {
        var result = new List<QuotaWindow>(); var i = 0;
        foreach (var x in Array(data, "limits").Take(64))
        {
            var unit = Num(x, "unit"); var count = Num(x, "number") ?? (unit == 3 ? 5 : 1);
            var scale = unit switch { 1 => 60, 3 => 3600, 4 => 86400, 5 => 2592000, 6 => 604800, _ => 0 };
            var span = count is > 0 and < 100000 && scale > 0 ? (long)(scale * count) : 0;
            var label = Text(x, "type").Equals("TIME_LIMIT", StringComparison.OrdinalIgnoreCase) ? "MCP · 月度" : span switch { 18000 => "5 小时", 604800 => "每周", 2592000 => "每月", > 0 and < 86400 => span / 3600 + " 小时", > 0 => span / 86400 + " 天", _ => "Credits" };
            var limit = Num(x, "usage"); var used = Num(x, "remaining") is { } left && limit is > 0 ? limit - left : Num(x, "currentValue");
            result.Add(Window("zcode:coding:" + i++, label + (limit == 0 ? " · 不限量" : ""), used, limit, Stamp(Num(x, "nextResetTime"), millis: true), span > 0 ? span : null, observed,
                limit == 0 ? null : Num(x, "percentage")));
        }
        return result.ToArray();
    }

    internal static (string Plan, QuotaWindow[] Windows) ParseBalance(JsonElement data, DateTimeOffset observed)
    {
        var now = Num(data, "server_time") ?? observed.ToUnixTimeSeconds();
        var plans = Array(data, "plans").Take(64).Where(p => Text(p, "status").Equals("active", StringComparison.OrdinalIgnoreCase)
            && (Numeric(p, "starts_at") is not { } start || start <= now) && (Numeric(p, "ends_at") is not { } end || end <= 0 || end > now)).ToArray();
        var windows = new List<QuotaWindow>(); var i = 0;
        foreach (var x in Array(data, "balances").Take(128))
        {
            var owner = plans.FirstOrDefault(p => Text(x, "user_plan_id").Length > 0 && Text(p, "user_plan_id").Length > 0 ? Text(x, "user_plan_id") == Text(p, "user_plan_id") : Text(x, "plan_id") == Text(p, "plan_id"));
            if (owner.ValueKind == JsonValueKind.Undefined || Numeric(x, "expires_at") is { } expiry && expiry > 0 && expiry <= now) continue;
            var total = Numeric(x, "total_units"); var left = Numeric(x, "remaining_units"); var used = Numeric(x, "used_units") ?? (total.HasValue && left.HasValue ? total - left : null);
            if (!total.HasValue && !used.HasValue && !left.HasValue) continue;
            var label = Safe(Text(x, "show_name")); if (label.Length == 0) label = "赠送 Credits";
            var plan = Safe(Text(owner, "name")); if (plan.Length > 0) label += " · " + plan;
            long? span = null;
            foreach (var e in Array(owner, "entitlements")) if (Text(e, "entitlement_id") == Text(x, "entitlement_id"))
                span = Text(e, "period").ToLowerInvariant() switch { "daily" or "day" => 86400, "weekly" or "week" => 604800, "monthly" or "month" => 2592000, _ => null };
            if (span is null && Numeric(x, "period_start") is { } begin && Numeric(x, "period_end") is { } end && end > begin && end - begin <= long.MaxValue) span = (long)(end - begin);
            // A supplied zero remaining is authoritative even if the count fields lag behind it.
            if (left is <= 0 && total is > 0) used = Math.Max(used ?? 0, total.Value);
            windows.Add(Window("zcode:gift:" + i++, label, used, total, Stamp(Numeric(x, "expires_at")), span, observed));
        }
        return (plans.Select(p => Safe(Text(p, "name"))).FirstOrDefault(p => p.Length > 0) ?? "ZCode Start Plan", windows.ToArray());
    }

    internal static string Decrypt(byte[] key, string value)
    {
        if (!value.StartsWith("enc:v1:", StringComparison.Ordinal)) return "";
        try
        {
            var parts = value[7..].Split('.'); if (parts.Length != 3) return "";
            var iv = Base64(parts[0]); var tag = Base64(parts[1]); var data = Base64(parts[2]);
            if (iv.Length != 12 || tag.Length != 16) return "";
            var clear = new byte[data.Length]; using var cipher = new AesGcm(key, 16); cipher.Decrypt(iv, data, tag, clear);
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException) { return ""; }
    }
    private static byte[] Base64(string s) { s = s.TrimEnd('=').Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }
    private static JsonElement Parse(string value) { try { using var doc = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 }); return doc.RootElement.Clone(); } catch (JsonException) { return default; } }
    private static double? Numeric(JsonElement element, string key) => Num(element, key) ?? (double.TryParse(Text(element, key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null);
    private static string Identity(JsonElement value) => new[] { "id", "user_id", "uid" }.Select(k => Field(value, k).ToString()).FirstOrDefault(s => s.Length > 0) ?? "";
    private static async Task<JsonElement> Optional(string path, CancellationToken token) { try { return await ReadJson(path, token); } catch (FileNotFoundException) { return default; } catch (DirectoryNotFoundException) { return default; } }
}
