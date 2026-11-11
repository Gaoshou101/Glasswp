using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WallpaperBridge;

internal sealed class LocalGoogleQuotaReader(HttpClient client, string? userHome = null)
{
    private readonly LocalAntigravityCliQuotaReader antigravity = new(userHome: userHome);
    private const string GeminiClient = "681255809395-oo8ft2oprdrnp9e3aqf6av3hmdib135j.apps.googleusercontent.com";
    private sealed record Login(string Path, string Token, string Identity, string Project, bool Expired);
    private string Home => userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Text(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
    private static JsonElement Field(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) ? value : default;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonElement Claims(string token)
    {
        try
        {
            var parts = token.Split('.'); if (parts.Length != 3 || parts[1].Length > 32768) return default;
            var payload = parts[1].Replace('-', '+').Replace('_', '/'); payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload)); return document.RootElement.Clone();
        }
        catch (Exception e) when (e is JsonException or FormatException) { return default; }
    }
    private static async Task<JsonElement> ReadJson(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(bytes, token)) > 0)
        { if (buffer.Length + count > 1024 * 1024) throw new JsonException(); buffer.Write(bytes, 0, count); }
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException(); return document.RootElement.Clone();
    }
    private static async Task<JsonElement?> FileJson(string path, CancellationToken token)
    {
        try { await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return await ReadJson(file, token); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
    private async Task<Login?> ReadLogin(CancellationToken token)
    {
        var directory = Path.Combine(Home, ".gemini");
        var settings = await FileJson(Path.Combine(directory, "settings.json"), token);
        var type = settings is null ? "" : Text(Field(Field(settings.Value, "security"), "auth"), "selectedType");
        if (type.Length > 0 && type != "oauth-personal") return null;
        var path = Path.Combine(directory, "oauth_creds.json"); var credentials = await FileJson(path, token);
        if (credentials is null) return null;
        var access = Text(credentials.Value, "access_token"); if (access.Length == 0) return null;
        var claims = Claims(Text(credentials.Value, "id_token")); var app = Text(claims, "azp"); if (app.Length == 0) app = Text(claims, "aud");
        if (app.Length > 0 && app != GeminiClient) return null;
        var identity = Text(claims, "sub"); if (identity.Length == 0) identity = "token:" + access;
        var expiry = Field(credentials.Value, "expiry_date");
        var expired = expiry.ValueKind == JsonValueKind.Number && expiry.TryGetDouble(out var stamp) && stamp <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var project = Text(credentials.Value, "project");
        // Gemini CLI's own per-user .env is a declarative project selection, never an endpoint.
        var envPath = Path.Combine(directory, ".env");
        if (File.Exists(envPath))
        {
            if (new FileInfo(envPath).Length > 1024 * 1024) throw new IOException();
            foreach (var line in await File.ReadAllLinesAsync(envPath, token))
            {
                var trimmed = line.Trim(); if (!trimmed.StartsWith("GOOGLE_CLOUD_PROJECT=", StringComparison.Ordinal)) continue;
                project = trimmed["GOOGLE_CLOUD_PROJECT=".Length..].Trim().Trim('"', '\'');
            }
        }
        if (userHome is null && Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") is { Length: > 0 } environmentProject) project = environmentProject;
        return new(path, access, identity, project, expired);
    }
    public async Task<QuotaData> Read(CancellationToken token)
    {
        var gemini = ReadGemini(token);
        var antigravityRead = antigravity.Read(token);
        var results = await Task.WhenAll(gemini, antigravityRead);
        var available = results.Where(r => r.Status != "unconfigured").ToArray();
        if (available.Length == 0) return new("unconfigured", null, string.Join("；", results.Select(r => r.Message)), []);
        var ok = available.Count(r => r.Status == "ok");
        return new(ok == available.Length ? "ok" : ok > 0 ? "stale" : available.Any(r => r.Status == "unauthorized") ? "unauthorized" : "offline",
            ok > 0 ? DateTimeOffset.UtcNow : null, string.Join("；", results.Select(r => r.Message)), available.SelectMany(r => r.Accounts).ToArray());
    }
    internal async Task<QuotaData> ReadGemini(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Login? login = null; string plan = "";
        QuotaAccount[] Known() => login is null ? [] : [new("local:gemini:" + Hash(login.Identity), "Gemini CLI 本机", "gemini", plan, [])];
        try
        {
            login = await ReadLogin(token);
            if (login is null) return new("unconfigured", null, "Gemini CLI：未发现订阅 OAuth 登录", []);
            if (login.Expired) return new("unauthorized", null, "Gemini CLI：登录已过期，请在原客户端重新登录", Known());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var load = await Call("loadCodeAssist", login, new { metadata = new { ideType = "IDE_UNSPECIFIED", platform = "PLATFORM_UNSPECIFIED", pluginType = "GEMINI" }, cloudaicompanionProject = login.Project.Length > 0 ? login.Project : null }, timeout.Token);
            plan = Tier(load, login);
            var project = Text(load, "cloudaicompanionProject");
            if (project.Length == 0) project = Text(Field(load, "cloudaicompanionProject"), "id");
            if (project.Length == 0) project = login.Project;
            if (project.Length == 0) return new("offline", null, "Gemini CLI：已读取套餐，但服务未提供额度项目；请在原客户端完成设置", Known());
            var quota = await Call("retrieveUserQuota", login, new { project }, timeout.Token);
            var current = await ReadLogin(timeout.Token);
            if (current is null || current.Token != login.Token || current.Identity != login.Identity || current.Project != login.Project)
                return new("unconfigured", null, "Gemini CLI：登录已切换，等待重新读取", []);
            var observed = DateTimeOffset.UtcNow;
            return new("ok", observed, "Gemini CLI：已读取本地套餐和额度", [Known().Single() with { Windows = ParseWindows(quota, observed) }]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { return new("unauthorized", null, "Gemini CLI：登录或额度查询权限不可用", Known()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or OperationCanceledException or ArgumentException)
        { return new("offline", null, "Gemini CLI：套餐或额度暂不可用", Known()); }
    }
    private async Task<JsonElement> Call(string method, Login login, object body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://cloudcode-pa.googleapis.com/v1internal:" + method);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        request.Headers.UserAgent.ParseAdd("GeminiCLI/0.61.0"); request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final != request.RequestUri) throw new HttpRequestException();
        using var stream = await response.Content.ReadAsStreamAsync(token); return await ReadJson(stream, token);
    }
    private static string Tier(JsonElement load, Login login)
    {
        var tier = Field(load, "paidTier"); if (tier.ValueKind != JsonValueKind.Object) tier = Field(load, "currentTier");
        var id = Text(tier, "id");
        return id switch { "g1-pro-tier" => "Google AI Pro", "g1-ultra-tier" => "Google AI Ultra", "free-tier" => "Gemini Code Assist Free",
            "standard-tier" => "Gemini Code Assist Standard", "enterprise-tier" => "Gemini Code Assist Enterprise", _ => SafeLabel(Text(tier, "name"), login.Token, login.Identity, login.Project) };
    }
    private static string SafeLabel(string label, params string[] secrets) => label.Length <= 80 && !label.Any(char.IsControl) && !label.Contains('@') &&
        !label.Contains("://", StringComparison.Ordinal) && !label.Contains("www.", StringComparison.OrdinalIgnoreCase) &&
        !Regex.IsMatch(label, @"(?:eyJ[A-Za-z0-9_-]*\.|\b(?:sk-|ghp_|gho_|github_pat_))", RegexOptions.CultureInvariant) &&
        !secrets.Any(secret => secret.Length > 0 && label.Contains(secret, StringComparison.Ordinal)) ? label : "";
    internal static QuotaWindow[] ParseWindows(JsonElement quota, DateTimeOffset observed)
    {
        var buckets = Field(quota, "buckets"); if (buckets.ValueKind != JsonValueKind.Array) throw new JsonException();
        var result = new Dictionary<string, QuotaWindow>(StringComparer.Ordinal);
        foreach (var bucket in buckets.EnumerateArray())
        {
            if (bucket.ValueKind != JsonValueKind.Object) continue;
            var model = Text(bucket, "modelId"); var tokenType = Text(bucket, "tokenType"); var key = model + ":" + tokenType;
            var fraction = QuotaAdapter.Number(bucket, "remainingFraction");
            if (model.Length == 0 || model.Length > 120 || !model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/') || fraction is not (>= 0 and <= 1)) continue;
            var reset = ClaudeQuotaAdapter.Reset(Field(bucket, "resetTime"));
            var item = new QuotaWindow("gemini:" + Hash(key), model, fraction * 100, null, null, "", reset, null, "unknown", reset is null ? "unknown" : "exact", observed, false);
            if (!result.TryGetValue(key, out var prior) || item.RemainingPercent < prior.RemainingPercent) result[key] = item;
        }
        if (result.Count == 0) throw new JsonException(); return result.Values.ToArray();
    }
}
