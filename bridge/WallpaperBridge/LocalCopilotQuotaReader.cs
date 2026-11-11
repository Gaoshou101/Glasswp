using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WallpaperBridge;

// Copilot editor/CLI discovery follows Magpie's account.go and copilot_cli.go.
// This reader performs entitlement GETs only, and never requests Copilot inference tokens.
internal sealed class LocalCopilotQuotaReader(HttpClient client, string? userHome = null)
{
    private sealed record Login(string Path, string Token, string User, bool Cli);
    private sealed record Result(string Status, string Message, QuotaAccount? Account);
    private string Home => userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string ConfigHome => userHome is null && Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } directory ? directory : Path.Combine(Home, ".config");
    private string CliHome => userHome is null && Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } directory ? directory : Path.Combine(Home, ".copilot");
    private static string Text(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
    private static JsonElement Field(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) ? value : default;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static double? Number(JsonElement node, string key)
    {
        var value = Field(node, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n
            : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && double.IsFinite(n) ? n : null;
    }
    private static async Task<JsonElement?> ReadFile(string path, CancellationToken token)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await ReadJson(file, token);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
    private static async Task<JsonElement> ReadJson(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(bytes, token)) > 0)
        { if (buffer.Length + count > 1024 * 1024) throw new JsonException(); buffer.Write(bytes, 0, count); }
        using var doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        return doc.RootElement.Clone();
    }
    private async Task<Login[]> Discover(CancellationToken token)
    {
        var found = new List<Login>();
        foreach (var name in new[] { "apps.json", "hosts.json" })
        {
            var path = Path.Combine(ConfigHome, "github-copilot", name);
            var root = await ReadFile(path, token);
            if (root is null) continue;
            foreach (var app in root.Value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                // Exact host boundary: github.com.evil and file-supplied enterprise URLs are never trusted.
                var host = app.Name.Split(':', 2)[0];
                var access = Text(app.Value, "oauth_token");
                if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && access.Length > 0)
                    found.Add(new(path, access, Text(app.Value, "user"), false));
            }
        }
        var cliPath = Path.Combine(CliHome, "config.json");
        var config = await ReadFile(cliPath, token);
        if (config is not null)
        {
            var tokens = Field(config.Value, "copilotTokens");
            var users = new List<JsonElement>();
            if (Field(config.Value, "lastLoggedInUser") is { ValueKind: JsonValueKind.Object } last) users.Add(last);
            if (Field(config.Value, "loggedInUsers") is { ValueKind: JsonValueKind.Array } list) users.AddRange(list.EnumerateArray());
            foreach (var user in users)
            {
                var login = Text(user, "login");
                var host = Text(user, "host").TrimEnd('/');
                if (login.Length == 0 || host is not ("https://github.com" or "github.com")) continue;
                var access = Text(tokens, "https://github.com:" + login);
                if (access.Length > 0) found.Add(new(cliPath, access, login, true));
            }
        }
        return found.DistinctBy(l => l.User.Length > 0 ? "user:" + l.User.ToLowerInvariant() : "token:" + Hash(l.Token)).Take(16).ToArray();
    }
    public async Task<QuotaData> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Login[] logins;
        try { logins = await Discover(token); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new("offline", null, "GitHub Copilot：本地登录文件无法读取", []); }
        if (logins.Length == 0) return new("unconfigured", null, "GitHub Copilot：未发现可读取的本地登录", []);
        var results = new List<Result>();
        // Bound network concurrency and avoid simultaneous queries against one GitHub login.
        foreach (var login in logins) results.Add(await Fetch(login, token));
        var accounts = results.Where(r => r.Account is not null).Select(r => r.Account!).DistinctBy(a => a.Id).ToArray();
        var ok = results.Count(r => r.Status == "ok");
        return new(ok == results.Count ? "ok" : ok > 0 ? "stale" : results.Any(r => r.Status == "unauthorized") ? "unauthorized" : "offline",
            ok > 0 ? DateTimeOffset.UtcNow : null, string.Join("；", results.Select(r => r.Message).Distinct()), accounts);
    }
    private async Task<JsonElement> Get(string url, Login login, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", login.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("GitHubCopilotChat/0.35.0");
        request.Headers.Add("Editor-Version", "vscode/1.107.0");
        if (login.Cli) request.Headers.Add("Copilot-Integration-Id", "copilot-developer-cli");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final != request.RequestUri) throw new HttpRequestException();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        return await ReadJson(stream, token);
    }
    private async Task<Result> Fetch(Login login, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            // /user supplies the account identity authenticated by this exact token; file user labels can be stale.
            var who = await Get("https://api.github.com/user", login, timeout.Token);
            var id = Number(who, "id")?.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(id)) throw new JsonException();
            var usage = await Get("https://api.github.com/copilot_internal/user", login, timeout.Token);
            if (!(await Discover(timeout.Token)).Any(current => current.Path == login.Path && current.Token == login.Token))
                return new("offline", "GitHub Copilot：登录已切换，等待重新读取", null);
            return new("ok", "GitHub Copilot：已读取本地套餐和额度", Parse(usage, "local:copilot:" + Hash(id), DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { return new("unauthorized", "GitHub Copilot：登录或订阅权限不可用", null); }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or ArgumentException)
        { return new("offline", "GitHub Copilot：套餐或额度暂不可用", null); }
    }
    internal static QuotaAccount Parse(JsonElement payload, string id, DateTimeOffset observed)
    {
        var plan = Text(payload, "access_type_sku") switch { "free_educational_quota" => "Education", "free_limited_copilot" => "Free", _ => Text(payload, "copilot_plan") switch {
            "free" => "Free", "individual" => "Pro", "individual_pro" => "Pro+", "individual_max" => "Max", "individual_edu" => "Education",
            "business" => "Business", "enterprise" => "Enterprise", _ => "" } };
        var snapshots = Field(payload, "quota_snapshots");
        if (plan.Length == 0 && snapshots.ValueKind != JsonValueKind.Object && Field(payload, "monthly_quotas").ValueKind != JsonValueKind.Object) throw new JsonException();
        var resets = ClaudeQuotaAdapter.Reset(Field(payload, "quota_reset_date_utc")) ?? ClaudeQuotaAdapter.Reset(Field(payload, "quota_reset_date"));
        var windows = new List<QuotaWindow>();
        foreach (var (key, label) in new[] { ("premium_interactions", "高级请求"), ("chat", "聊天请求"), ("completions", "代码补全") })
        {
            var quota = Field(snapshots, key); if (quota.ValueKind != JsonValueKind.Object) continue;
            var ownBilling = Field(quota, "token_based_billing");
            var credits = key == "premium_interactions" && (ownBilling.ValueKind == JsonValueKind.True || ownBilling.ValueKind != JsonValueKind.False && Field(payload, "token_based_billing").ValueKind == JsonValueKind.True);
            var unit = credits ? "credits" : "requests";
            var name = credits ? "高级积分" : label;
            var unlimited = Field(quota, "unlimited").ValueKind == JsonValueKind.True;
            var spentPool = unlimited && key == "premium_interactions" && Field(quota, "has_quota").ValueKind == JsonValueKind.False;
            var limit = Number(quota, "entitlement");
            if (!unlimited && limit is <= 0) continue;
            var remaining = Number(quota, "quota_remaining") ?? Number(quota, "remaining");
            var percent = spentPool ? 0 : unlimited ? (double?)null : limit is > 0 && remaining is not null ? Math.Clamp(remaining.Value / limit.Value * 100, 0, 100)
                : Number(quota, "percent_remaining") is { } p ? Math.Clamp(p, 0, 100) : null;
            if (!unlimited && percent is null) continue;
            var reset = ClaudeQuotaAdapter.Reset(Field(quota, "quota_reset_at")) ?? resets;
            var unlimitedUsed = credits && Number(quota, "credits_used") is >= 0 and var creditsUsed ? creditsUsed : (double?)null;
            windows.Add(new(key, name + (unlimited && !spentPool ? " · 不限量" : ""), percent,
                !unlimited && limit is > 0 && remaining is not null ? Math.Max(0, limit.Value - remaining.Value) : unlimited && !spentPool ? unlimitedUsed : null,
                !unlimited && limit is > 0 ? limit : null, unit, reset, null,
                unlimited && !spentPool ? "non_window" : "fixed", reset is null ? "unknown" : "exact", observed, false,
                DisplayText: unlimited && !spentPool ? "不限量" : null));
        }
        // Copilot Free's older response exposes monthly totals and remaining values,
        // before quota_snapshots existed. Use only measured finite allowances.
        if (snapshots.ValueKind != JsonValueKind.Object)
        {
            var totals = Field(payload, "monthly_quotas"); var left = Field(payload, "limited_user_quotas");
            var reset = ClaudeQuotaAdapter.Reset(Field(payload, "limited_user_reset_date"));
            foreach (var (key, label) in new[] { ("chat", "聊天请求"), ("completions", "代码补全") })
                if (Number(totals, key) is > 0 and var limit && Number(left, key) is >= 0 and var remaining)
                    windows.Add(new(key, label, Math.Clamp(remaining / limit * 100, 0, 100), Math.Max(0, limit - remaining), limit, "requests", reset, null, "fixed", reset is null ? "unknown" : "exact", observed, false));
        }
        return new(id, "GitHub Copilot 本机", "copilot", plan.Length > 0 ? "GitHub Copilot " + plan : "", windows.ToArray());
    }
}
