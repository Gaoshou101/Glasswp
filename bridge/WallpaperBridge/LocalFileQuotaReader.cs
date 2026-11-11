using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WallpaperBridge;

// Native login files only. Endpoint contracts are pinned in docs/AI-SOURCES-2026-11-10.md.
internal sealed class LocalFileQuotaReader(HttpClient client, string? userHome = null, Func<string>? machineIdentity = null)
{
    private readonly string instance = Guid.NewGuid().ToString("N");
    private readonly Lazy<string> machine = new(() => machineIdentity?.Invoke() ?? (userHome is null ? DeviceIdentity() : ""));
    private (string Identity, DateTimeOffset Until, QuotaData Data)? deepaiCache;
    private string Home => userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string Roaming => userHome is null ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : Path.Combine(Home, "AppData", "Roaming");
    private string EnvPath(string variable, string fallback) => userHome is null && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? Path.GetFullPath(value) : fallback;
    private sealed record Credential(string Provider, string Path, string Token, string Identity, JsonElement Data);
    internal static JsonElement F(JsonElement j, string key) => j.ValueKind == JsonValueKind.Object && j.TryGetProperty(key, out var v) ? v : default;
    internal static string T(JsonElement j, string key) { var v = F(j, key); return v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : ""; }
    internal static double? N(JsonElement j, string key) => double.TryParse(T(j, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
    private static bool B(JsonElement j, string key) => F(j, key).ValueKind == JsonValueKind.True;
    internal static DateTimeOffset? D(JsonElement j, string key)
    {
        if (N(j, key) is > 0 and var n) { try { return DateTimeOffset.FromUnixTimeMilliseconds((long)(n > 1e12 ? n : n * 1000)); } catch (ArgumentOutOfRangeException) { return null; } }
        return DateTimeOffset.TryParse(T(j, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }
    internal static DateTimeOffset? AtomDate(JsonElement j, string key)
    {
        var raw = T(j, key);
        if (Regex.IsMatch(raw, @"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(\.\d+)?$"))
            return DateTimeOffset.TryParse(raw + "+08:00", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) ? local : null;
        return D(j, key);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Label(string value) => value.Length > 100 || value.Contains('@') || value.Any(char.IsControl) || Regex.IsMatch(value, "(?i)bearer |sk-|eyJ|https?://|[a-z0-9_-]{40,}") ? "" : value;
    private static readonly Dictionary<string, string> Names = new() { ["commandcode-plan"] = "Command Code", ["grok"] = "Grok Build", ["devin"] = "Devin", ["catpaw"] = "妙手 CatPaw", ["raccoon"] = "小浣熊", ["atomcode"] = "AtomCode", ["codar"] = "科搭 Codar", ["deepaicode"] = "DeepAI Code" };
    private static async Task<string> ReadText(string path, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 1024 * 1024) throw new JsonException();
        using var reader = new StreamReader(file); var text = new StringBuilder(); var buffer = new char[8192]; int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0) { if (text.Length + count > 1024 * 1024) throw new JsonException(); text.Append(buffer, 0, count); }
        return text.ToString();
    }
    private static JsonElement Parse(string text) { using var doc = JsonDocument.Parse(text.TrimStart('\uFEFF'), new() { MaxDepth = 32 }); return doc.RootElement.Clone(); }
    private static JsonElement Toml(string text)
    {
        var data = new Dictionary<string, string>(); var section = "";
        foreach (var line in text.Split('\n'))
        {
            if (Regex.Match(line, "^\\s*\\[([^]]+)\\]\\s*(?:#.*)?$") is { Success: true } header) { section = header.Groups[1].Value; continue; }
            var m = Regex.Match(line, "^\\s*([a-zA-Z0-9_]+)\\s*=\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|'[^']*'|[0-9.]+)\\s*(?:#.*)?$");
            if (!m.Success) continue;
            var key = (section.Length > 0 ? section + "." : "") + m.Groups[1].Value;
            if (!data.TryAdd(key, m.Groups[2].Value.StartsWith('"') ? JsonSerializer.Deserialize<string>(m.Groups[2].Value)! : m.Groups[2].Value.Trim('\''))) throw new JsonException();
        }
        return JsonSerializer.SerializeToElement(data);
    }
    private async Task<Credential?> Login(string provider, CancellationToken token)
    {
        var path = provider switch {
            "commandcode-plan" => Path.Combine(Home, ".commandcode", "auth.json"),
            "grok" => Path.Combine(EnvPath("GROK_HOME", Path.Combine(Home, ".grok")), "auth.json"),
            "devin" => Path.Combine(Roaming, "devin", "credentials.toml"),
            "catpaw" => EnvPath("CATPAW_AUTH_PATH", Path.Combine(Home, ".meituan-catpaw", "auth.json")),
            "atomcode" => Path.Combine(Home, ".atomcode", "auth.toml"),
            "codar" => Path.Combine(EnvPath("CODAR_HOME", Path.Combine(Home, ".codar")), "config.json"),
            "deepaicode" => DeepaiPath(),
            _ => RaccoonPath()
        };
        if (!File.Exists(path)) return null;
        var raw = await ReadText(path, token); var data = path.EndsWith(".toml") ? Toml(raw) : Parse(raw);
        string access, identity;
        switch (provider)
        {
            case "commandcode-plan": access = T(data, "apiKey"); identity = T(data, "userId"); break;
            case "grok":
                data = data.ValueKind == JsonValueKind.Object ? data.EnumerateObject().OrderBy(p => p.Name).Select(p => p.Value).FirstOrDefault(v => T(v, "key").Length > 0) : default;
                access = T(data, "key"); identity = T(data, "user_id");
                if (D(data, "expires_at") is { } grokExpiry && grokExpiry <= DateTimeOffset.UtcNow) throw new UnauthorizedAccessException();
                break;
            case "devin": access = T(data, "windsurf_api_key"); identity = ""; break;
            case "catpaw": access = T(F(data, "auth"), "accessToken"); identity = T(F(data, "account"), "uid"); break;
            case "atomcode":
                access = T(data, "access_token"); identity = T(data, "user.id");
                if (N(data, "created_at") is { } created && N(data, "expires_in") is > 0 and var lifetime && created + lifetime <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) throw new UnauthorizedAccessException();
                break;
            case "codar":
                access = T(F(F(data, "provider"), "speedai"), "token"); var claims = Claims(access); identity = T(claims, "username");
                if (identity.Length == 0 || D(claims, "exp") is { } expires && expires <= DateTimeOffset.UtcNow) throw new UnauthorizedAccessException();
                break;
            case "deepaicode": access = T(data, "apiKey"); identity = T(data, "username"); if (access.Length > 512 || access.Any(char.IsWhiteSpace)) throw new JsonException(); break;
            default: access = T(data, "access_token"); if (access.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) access = access[7..]; identity = ""; break;
        }
        if (access.Length == 0) return null;
        if (access.Length > 32768 || access.Any(char.IsControl)) throw new JsonException();
        return new(provider, path, access, identity.Length > 0 ? identity : access, data);
    }
    private string DeepaiPath()
    {
        var roots = new List<string>();
        if (userHome is null) foreach (var key in new[] { "T3CODE_HOME", "DEEPAI_CODE_HOME" }) if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } dir) roots.Add(dir);
        roots.Add(Path.Combine(Home, ".deepai-code"));
        return roots.SelectMany(r => new[] { Path.Combine(r, "userdata", "secrets"), Path.Combine(r, "secrets"), Path.Combine(r, "dev", "secrets") })
            .SelectMany(r => new[] { Path.Combine(r, "deepai-account.bin"), Path.Combine(r, "deepai-account-user-managed.bin") }).FirstOrDefault(File.Exists) ?? Path.Combine(Home, ".deepai-code", "userdata", "secrets", "deepai-account.bin");
    }
    private static JsonElement Claims(string jwt)
    {
        var parts = jwt.Split('.'); if (parts.Length != 3) throw new JsonException();
        var payload = parts[1].Replace('-', '+').Replace('_', '/'); payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        try { return Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))); } catch (FormatException) { throw new JsonException(); }
    }
    private string RaccoonPath()
    {
        foreach (var key in new[] { "RACCOON_BOX_AGENT_CONFIG_DIR", "BOX_AGENT_CONFIG_DIR" })
            if (userHome is null && Environment.GetEnvironmentVariable(key) is { Length: > 0 } dir) return Path.Combine(dir, "auth.json");
        return new[] { Path.Combine(Home, ".office-raccoon", "agent", "config", "auth.json"), Path.Combine(Roaming, "office-raccoon", "agent", "config", "auth.json"), Path.Combine(Home, ".box-agent", "config", "auth.json") }.FirstOrDefault(File.Exists) ?? Path.Combine(Home, ".box-agent", "config", "auth.json");
    }
    public async Task<QuotaData> Read(CancellationToken token)
    {
        var results = await Task.WhenAll(Names.Keys.Select(p => ReadProvider(p, token)));
        var found = results.Where(r => r.Status != "unconfigured").ToArray();
        return found.Length == 0 ? new("unconfigured", null, "未检测到其他本机 CLI 登录", []) : DataHub.Merge(found);
    }
    private async Task<QuotaData> ReadProvider(string provider, CancellationToken token)
    {
        string cacheIdentity = "";
        try
        {
            var login = await Login(provider, token);
            if (login is null) return new("unconfigured", null, "", []);
            cacheIdentity = Hash(login.Token);
            if (provider == "deepaicode" && deepaiCache is { } cached && cached.Identity == cacheIdentity && cached.Until > DateTimeOffset.UtcNow) return cached.Data;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var account = provider switch {
                "commandcode-plan" => await CommandCode(login, deadline.Token), "grok" => await Grok(login, deadline.Token),
                "devin" => await Devin(login, deadline.Token), "catpaw" => await Catpaw(login, deadline.Token),
                "raccoon" => await Raccoon(login, deadline.Token), "atomcode" => await Atomcode(login, deadline.Token),
                "codar" => await Codar(login, deadline.Token), _ => await Deepai(login, deadline.Token)
            };
            var current = await Login(provider, token);
            if (current?.Token != login.Token || current.Identity != login.Identity) return new("empty", null, Names[provider] + " 登录已切换，等待重读", []);
            var result = new QuotaData(account.Windows.Length == 0 ? "stale" : "ok", DateTimeOffset.UtcNow, Names[provider] + (account.Windows.Length == 0 ? " 已读取套餐，额度暂不可用" : " 已读取"), [account]);
            if (provider == "deepaicode") deepaiCache = (cacheIdentity, DateTimeOffset.UtcNow.AddMinutes(10), result);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or ArgumentException or InvalidOperationException or OverflowException)
        {
            var auth = e is UnauthorizedAccessException || e is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };
            var result = new QuotaData(auth ? "unauthorized" : "offline", null, Names[provider] + (auth ? " 登录不可用，请回原客户端检查" : " 本机套餐暂不可读取"), []);
            if (provider == "deepaicode" && cacheIdentity.Length > 0) deepaiCache = (cacheIdentity, DateTimeOffset.UtcNow.AddMinutes(e is HttpRequestException { StatusCode: HttpStatusCode.Forbidden } ? 360 : 10), result);
            return result;
        }
    }
    private async Task<JsonElement> Request(string url, Credential login, CancellationToken token, object? body = null, string? customHeader = null, string? agent = null)
    {
        // URLs here are code constants. Never trust endpoint overrides from native credentials.
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (login.Provider != "devin") {
            if (customHeader is not null) request.Headers.Add(customHeader, login.Token);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        }
        request.Headers.Accept.Add(new("application/json"));
        if (agent is not null) request.Headers.UserAgent.ParseAdd(agent);
        if (login.Provider == "deepaicode") {
            request.Headers.Add("X-DeepAI-Code-Theos", "4"); request.Headers.Add("X-DeepAI-Code-Instance", instance);
            if (!string.IsNullOrWhiteSpace(machine.Value)) request.Headers.Add("X-DeepAI-Code-Machine", Hash(machine.Value.Trim().ToLowerInvariant()));
        }
        if (login.Provider == "devin") request.Headers.Add("Connect-Protocol-Version", "1");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri) throw new HttpRequestException();
        await using var stream = await response.Content.ReadAsStreamAsync(token); using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0) { if (buffer.Length + read > 2 * 1024 * 1024) throw new JsonException(); buffer.Write(chunk, 0, read); }
        return Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }
    private static QuotaAccount Account(Credential c, string plan, IEnumerable<QuotaWindow> windows) => new("local:" + c.Provider + ":" + Hash(c.Identity), Names[c.Provider] + " 本机", c.Provider,
        plan.Contains(c.Token, StringComparison.Ordinal) || c.Identity.Length >= 8 && plan.Contains(c.Identity, StringComparison.Ordinal) ? "" : Label(plan), windows.ToArray());
    internal static QuotaWindow Window(string id, string label, double? used = null, double? limit = null, double? percent = null, string unit = "", DateTimeOffset? reset = null, long? duration = null, double? balance = null) =>
        new(id, label, percent is { } p ? Math.Clamp(100 - p, 0, 100) : used is { } u && limit is > 0 ? Math.Clamp(100 - u / limit.Value * 100, 0, 100) : null,
            used, limit, unit, reset, duration, "fixed", reset is null ? "unknown" : "exact", DateTimeOffset.UtcNow, false, RemainingValue: balance);
    private async Task<QuotaAccount> CommandCode(Credential c, CancellationToken token)
    {
        async Task<JsonElement> Optional(string path) {
            try { var value = await Request("https://api.commandcode.ai/alpha/billing/" + path, c, token); return F(value, "success").ValueKind == JsonValueKind.False ? default : value; }
            catch (HttpRequestException) { return default; }
        }
        var responses = await Task.WhenAll(Optional("credits"), Optional("subscriptions")); var credits = responses[0]; var sub = F(responses[1], "data");
        if (credits.ValueKind != JsonValueKind.Object && responses[1].ValueKind != JsonValueKind.Object) throw new HttpRequestException();
        var cancelled = responses[1].ValueKind == JsonValueKind.Object && (T(sub, "status") is "canceled" or "incomplete_expired" || T(sub, "planId").Length == 0);
        var planId = T(sub, "planId"); if (planId.Length == 0 && !cancelled) planId = T(F(credits, "credits"), "planId");
        var plans = new (string Id, string Name, double Monthly, bool Units)[] { ("individual-provider", "Provider", 0, false), ("individual-pro-v1", "Pro", 80, true), ("individual-goat", "GOAT", 70, true), ("individual-ultra", "Ultra", 300, false), ("individual-max", "Max", 150, false), ("individual-pro", "Pro", 30, true), ("individual-go", "Go", 10, true), ("teams-pro", "Teams Pro", 40, false) };
        var plan = plans.FirstOrDefault(p => planId.Replace('_', '-').StartsWith(p.Id, StringComparison.OrdinalIgnoreCase)); var windows = new List<QuotaWindow>();
        foreach (var (key, label, span) in new[] { ("fiveHour", "5 小时窗口", 18000L), ("weekly", "周窗口", 604800L) }) {
            var w = F(F(credits, "windowLimits"), key); if (N(w, "used") is { } used && N(w, "cap") is > 0 and var cap) windows.Add(Window(key, label, used, cap, reset: D(w, "resetAt"), duration: span));
        }
        var pools = F(credits, "credits"); var monthly = N(pools, "monthlyCredits"); var purchased = N(pools, "purchasedCredits"); var free = N(pools, "freeCredits");
        if (monthly is not null || purchased is not null || free is not null) {
            var extra = (purchased ?? 0) + (free ?? 0); var left = monthly ?? 0;
            if (!cancelled && responses[1].ValueKind == JsonValueKind.Object && plan.Monthly > 0 && monthly is not null) {
                var total = Math.Max(plan.Monthly, left) + (plan.Units ? 0 : extra);
                windows.Add(Window("credits", "月度额度", total - left - (plan.Units ? 0 : extra), total, unit: plan.Units ? "credits" : "USD", reset: D(sub, "currentPeriodEnd")));
                if (plan.Units && extra > 0) windows.Add(Window("extra", "额外余额", unit: "USD", balance: extra));
            } else if (plan.Units) {
                if (monthly is not null) windows.Add(Window("balance", "可用积分", unit: "credits", balance: monthly));
                if (purchased is not null || free is not null) windows.Add(Window("extra", "额外余额", unit: "USD", balance: extra));
            } else windows.Add(Window("balance", "可用余额", unit: "USD", balance: left + extra));
        }
        return Account(c, cancelled ? "No plan" : plan.Name ?? planId, windows);
    }
    private async Task<QuotaAccount> Grok(Credential c, CancellationToken token)
    {
        var root = await Request("https://cli-chat-proxy.grok.com/v1/billing?format=credits", c, token); var cfg = F(root, "config");
        if (cfg.ValueKind != JsonValueKind.Object) throw new JsonException();
        var period = F(cfg, "currentPeriod"); var kind = T(period, "type").Replace("USAGE_PERIOD_TYPE_", "");
        var reset = D(period, "end") ?? D(cfg, "billingPeriodEnd");
        var windows = new List<QuotaWindow> { Window("allowance", kind switch { "DAILY" => "日额度", "WEEKLY" => "周额度", "MONTHLY" => "月额度", _ => "套餐额度" }, percent: N(cfg, "creditUsagePercent"), reset: reset, duration: kind switch { "DAILY" => 86400, "WEEKLY" => 604800, "MONTHLY" => 2592000, _ => null }) };
        if (N(F(cfg, "onDemandCap"), "val") is > 0 and var cap) windows.Add(Window("on-demand", "按需额度", N(F(cfg, "onDemandUsed"), "val"), cap, reset: reset));
        return Account(c, T(cfg, "planName"), windows);
    }
    private async Task<QuotaAccount> Devin(Credential c, CancellationToken token)
    {
        var root = await Request("https://server.codeium.com/exa.seat_management_pb.SeatManagementService/GetUserStatus", c, token,
            new { metadata = new { ideName = "devin-cli", ideVersion = "3000.11.3", extensionName = "devin-cli", extensionVersion = "3000.11.3", apiKey = c.Token, locale = "en", os = "windows" } });
        var user = F(root, "userStatus"); if (user.ValueKind != JsonValueKind.Object) throw new JsonException();
        var st = F(user, "planStatus"); var info = F(st, "planInfo"); var windows = new List<QuotaWindow>();
        foreach (var (key, label, span) in new[] { ("daily", "日额度", 86400L), ("weekly", "周额度", 604800L) }) {
            if (B(info, key == "daily" ? "hideDailyQuota" : "hideWeeklyQuota")) continue;
            var left = N(st, key + "QuotaRemainingPercent"); var reset = D(st, key + "QuotaResetAtUnix");
            if (left is null && T(info, "billingStrategy") == "BILLING_STRATEGY_QUOTA" && reset is not null) left = 0;
            if (left is not null) windows.Add(Window(key, label, percent: 100 - left, reset: reset, duration: span));
        }
        if (N(st, "acuLimit") is > 0 and var limit) windows.Add(Window("acu", "ACU 额度", N(st, "acuConsumed") ?? 0, limit, unit: "ACU", reset: D(st, "planEnd")));
        if (N(st, "overageBalanceMicros") is >= 0 and var extra) windows.Add(Window("extra", "额外余额", unit: "USD", balance: extra / 1e6));
        return Account(c, T(info, "planName").Length > 0 ? T(info, "planName") : T(user, "teamsTier").Replace("TEAMS_TIER_", "").Replace('_', ' '), windows);
    }
    private async Task<QuotaAccount> Catpaw(Credential c, CancellationToken token)
    {
        var root = await Request("https://catx.nocode.cn/api/gateway/credit/balance", c, token, customHeader: "X-Auth-Token");
        if (N(root, "code") is 4010 or 4011) throw new UnauthorizedAccessException();
        if (N(root, "code") != 0) throw new JsonException(); var data = F(root, "data"); var plan = F(data, "userPlan");
        var credits = N(data, "availableCredits") ?? N(data, "credit") ?? N(data, "balance") ?? N(data, "available") ?? throw new JsonException();
        return Account(c, T(plan, "planName"), [Window("credits", "可用积分", unit: "credits", balance: credits, reset: D(plan, "nextRefreshTime"))]);
    }
    private async Task<QuotaAccount> Raccoon(Credential c, CancellationToken token)
    {
        var balance = await Request("https://xiaohuanxiong.com/api/web/points/v1/balance", c, token); var data = F(balance, "data"); if (data.ValueKind != JsonValueKind.Object) data = balance;
        if (N(balance, "code") is 200001 or 200003) throw new UnauthorizedAccessException();
        if (N(balance, "code") is { } code && code != 0) throw new JsonException();
        var points = N(data, "available_points") ?? throw new JsonException();
        string plan = "";
        try {
            using var optional = CancellationTokenSource.CreateLinkedTokenSource(token); optional.CancelAfter(TimeSpan.FromSeconds(3));
            var entitlement = await Request("https://xiaohuanxiong.com/api/web/auth/v1/entitlement_info", c, optional.Token); var ent = F(entitlement, "data"); if (ent.ValueKind != JsonValueKind.Object) ent = entitlement;
            var office = F(ent, "office"); if (office.ValueKind != JsonValueKind.Object) office = F(ent, "code");
            if (office.ValueKind == JsonValueKind.Object) plan = B(office, "pro_enable") ? T(F(office, "active_plan"), "type") is { Length: > 0 } name ? name : "Pro" : "Free";
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException) { }
        return Account(c, plan, [Window("points", "可用积分", unit: "points", balance: points)]);
    }
    private async Task<QuotaAccount> Atomcode(Credential c, CancellationToken token)
    {
        var root = await Request("https://api.gitcode.com/api/v5/coding-plan/status-v2", c, token, agent: "atomcode/5.2.3");
        var plan = F(root, "codingplan_free"); if (plan.ValueKind != JsonValueKind.Object) plan = F(root, "plan");
        var raw = F(root, "rate_limit_windows"); var usage = F(root, "current_usage"); var windows = new List<QuotaWindow>();
        var rows = raw.ValueKind == JsonValueKind.Array ? raw.EnumerateArray().ToArray() : [];
        if (rows.Length > 128) throw new JsonException();
        string Unit(JsonElement w) => new[] { "unit", "quota_unit", "limit_type", "resource" }.Select(k => T(w, k)).FirstOrDefault(v => v.Length > 0)?.ToLowerInvariant() ?? "";
        double? First(JsonElement w, params string[] keys) => keys.Select(k => N(w, k)).FirstOrDefault(v => v is not null);
        var seen = new HashSet<string>();
        var candidates = rows.ToList();
        if (usage.ValueKind == JsonValueKind.Object) candidates.Add(usage);
        foreach (var w in candidates) {
            var seconds = N(w, "window_size_seconds") ?? (N(w, "window_hours") ?? 5) * 3600;
            if (seconds is <= 0 or > 31536000) continue;
            var tu = First(w, "window_tokens_used", "tokens_used", "token_count_used", "tokens", "used_tokens");
            var tl = First(w, "window_token_limit", "token_limit", "token_count_limit", "tokens_limit", "limit_tokens");
            var tp = First(w, "token_usage_percent", "tokens_usage_percent");
            var cu = First(w, "calls_used", "requests_used", "request_count_used"); var cl = First(w, "call_limit", "requests_limit", "request_count_limit"); var cp = First(w, "call_usage_percent", "requests_usage_percent");
            var unit = Unit(w); var isTokens = unit.Contains("token") || unit.Contains("词元") || unit.Length == 0 && cu is null && cl is null && cp is null && (tu is not null || tl is not null || tp is not null);
            var used = isTokens ? tu : cu; var limit = isTokens ? tl : cl; var pct = isTokens ? tp : cp ?? N(w, "usage_percent");
            var signature = (isTokens ? "tokens:" : "calls:") + seconds;
            if (w.Equals(usage) && seen.Contains(signature)) continue;
            seen.Add(signature);
            var reset = AtomDate(w, "reset_at") ?? AtomDate(usage, "reset_at"); if (N(w, "seconds_until_reset") is >= 0 and <= 31536000 and var after) reset = DateTimeOffset.UtcNow.AddSeconds(after);
            windows.Add(Window("window-" + windows.Count, (seconds / 3600).ToString(CultureInfo.InvariantCulture) + " 小时" + (isTokens ? " Token" : " 请求"), used, limit, pct, isTokens ? "tokens" : "calls", reset, (long)seconds));
        }
        if (plan.ValueKind != JsonValueKind.Object && windows.Count == 0) throw new JsonException();
        return Account(c, T(plan, "plan_name"), windows);
    }
    private async Task<QuotaAccount> Codar(Credential c, CancellationToken token)
    {
        async Task<JsonElement> Points(string path, object body) {
            var raw = await Request("https://api.kuaipaper.com" + path, c, token, body);
            if (N(raw, "code") is 401 or 403) throw new UnauthorizedAccessException();
            if (N(raw, "code") is { } code && code != 200) throw new JsonException();
            return F(raw, "data").ValueKind == JsonValueKind.Object ? F(raw, "data") : raw;
        }
        double? general = null, agent = null; Exception? failure = null;
        double? Pick(JsonElement data, params string[] keys) => keys.Select(k => N(data, k)).FirstOrDefault(n => n is >= 0);
        try { var data = await Points("/v1/gettoken", new { username = c.Identity, token = 0 }); general = Pick(data, "token", "remaining_points", "points", "balance"); agent = Pick(data, "agent_token", "agentToken", "agent_points", "agentPoints"); }
        catch (Exception e) when (e is HttpRequestException or JsonException) { failure = e; }
        if (agent is null) try { agent = Pick(await Points("/v1/agent/balance", new { username = c.Identity }), "balance", "points", "agent_balance", "agent_token", "agentToken", "agent_points", "agentPoints", "remaining_points"); }
        catch (Exception e) when (e is HttpRequestException or JsonException) { failure ??= e; }
        var windows = new List<QuotaWindow>();
        if (general is not null) windows.Add(Window("points", "通用点数", unit: "points", balance: general));
        if (agent is not null) windows.Add(Window("agent", "Agent 点数", unit: "points", balance: agent));
        if (windows.Count == 0) throw failure ?? new JsonException();
        return Account(c, "", windows);
    }
    private static string DeviceIdentity()
    {
        try {
            using var search = new System.Management.ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
            search.Options.Timeout = TimeSpan.FromSeconds(5);
            using var results = search.Get();
            foreach (System.Management.ManagementObject item in results) using (item) {
                var id = item["UUID"]?.ToString()?.ToLowerInvariant();
                if (Guid.TryParse(id, out _) && id is not ("00000000-0000-0000-0000-000000000000" or "ffffffff-ffff-ffff-ffff-ffffffffffff" or "03000200-0400-0500-0006-000700080009")) return id;
            }
        } catch (Exception e) when (e is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
        try { using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"); return key?.GetValue("MachineGuid") as string ?? ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return ""; }
    }
    private async Task<QuotaAccount> Deepai(Credential c, CancellationToken token)
    {
        var allowance = await Request("https://api.deepai.org/deepai_code/theos", c, token, customHeader: "Api-Key", agent: "wallpaper-dashboard/1.0");
        JsonElement summary = default;
        try {
            using var optional = CancellationTokenSource.CreateLinkedTokenSource(token); optional.CancelAfter(TimeSpan.FromSeconds(3));
            summary = await Request("https://api.deepai.org/get_usage_summary", c, optional.Token, customHeader: "Api-Key", agent: "wallpaper-dashboard/1.0");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException) { }
        var windows = new List<QuotaWindow>();
        if (N(allowance, "balance") is { } balance) {
            var granted = N(allowance, "granted");
            windows.Add(granted is > 0 ? Window("theos", "今日 Theos", Math.Max(0, granted.Value - balance), granted, unit: "theos", reset: D(allowance, "refresh_at")) : Window("theos", "可用 Theos", unit: "theos", balance: balance, reset: D(allowance, "refresh_at")));
        }
        if (N(summary, "credits_remaining") is { } credits) windows.Add(Window("credits", "可用积分", unit: "credits", balance: credits));
        if (windows.Count == 0) throw new JsonException();
        return Account(c, T(summary, "plan"), windows);
    }
}
