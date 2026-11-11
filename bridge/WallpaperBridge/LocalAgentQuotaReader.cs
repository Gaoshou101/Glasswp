using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WallpaperBridge;

// Reads the current user's CLI login only. Never refreshes, writes, or exports credentials.
internal sealed class LocalAgentQuotaReader(HttpClient client, string? userHome = null)
{
    private const int MaximumBytes = 1024 * 1024;
    private const string CodexUsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private sealed record Login(string Provider, string Alias, string Token, string Identity, string AccountId, string Plan, bool Expired);
    private sealed record Result(string Status, string Message, QuotaAccount? Account = null);
    private sealed class UnsupportedStoreException : Exception;

    public async Task<QuotaData> Read(bool codex, bool claude, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var reads = new List<Task<Result>>();
        if (codex) reads.Add(ReadProvider("codex", token));
        if (claude) reads.Add(ReadProvider("claude", token));
        if (reads.Count == 0) return new("unconfigured", null, "本地 AI agent 未启用", []);
        var results = await Task.WhenAll(reads);
        var available = results.Where(r => r.Status != "unconfigured").ToArray();
        var success = available.Count(r => r.Status == "ok");
        var status = available.Length == 0 ? "unconfigured" : success == available.Length ? "ok" : success > 0 ? "stale"
            : available.Any(r => r.Status == "unauthorized") ? "unauthorized" : "offline";
        return new(status, success > 0 ? DateTimeOffset.UtcNow : null,
            string.Join("；", results.Select(r => r.Message)), results.Where(r => r.Account is not null).Select(r => r.Account!).ToArray());
    }

    private string DirectoryFor(string provider)
    {
        // An explicit home is an isolated profile, including in tests; process overrides apply only to the real profile.
        var overridePath = userHome is null ? Environment.GetEnvironmentVariable(provider == "codex" ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR") : null;
        return !string.IsNullOrWhiteSpace(overridePath) ? Path.GetFullPath(overridePath)
            : Path.Combine(userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), provider == "codex" ? ".codex" : ".claude");
    }

    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()!.Trim() : "";
    private static JsonElement Field(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var field) ? field : default;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonElement Claims(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3 || parts[1].Length > 32768) return default;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload), new JsonDocumentOptions { MaxDepth = 32 });
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : default;
        }
        catch (Exception e) when (e is FormatException or JsonException) { return default; }
    }
    private static bool Expired(JsonElement node, string property, bool milliseconds = false)
    {
        var value = Field(node, property);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var stamp) || !double.IsFinite(stamp)) return false;
        var seconds = milliseconds && stamp > 1e11 ? stamp / 1000 : stamp;
        return seconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static async Task<JsonElement> ReadJson(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, token)) > 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new JsonException("Local quota document exceeds size limit");
            buffer.Write(bytes, 0, count);
        }
        using var doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Local quota object expected");
        return doc.RootElement.Clone();
    }
    private static async Task<JsonElement> ReadFile(string path, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
        if (file.Length > MaximumBytes) throw new JsonException("Local login document exceeds size limit");
        return await ReadJson(file, token);
    }

    private async Task<Login?> ReadLogin(string provider, CancellationToken token)
    {
        var directory = DirectoryFor(provider);
        if (provider == "codex" && !await UsesCodexFileStore(directory, token)) throw new UnsupportedStoreException();
        var root = await ReadFile(Path.Combine(directory, provider == "codex" ? "auth.json" : ".credentials.json"), token);
        if (provider == "codex")
        {
            // Legacy auth.json omitted auth_mode. Other current modes have different credential semantics.
            if (Text(root, "OPENAI_API_KEY").Length > 0 || Text(root, "auth_mode") is not ("" or "chatgpt")) return null;
            var tokens = Field(root, "tokens");
            var access = Text(tokens, "access_token");
            if (access.Length == 0) return null;
            var idClaims = Claims(Text(tokens, "id_token"));
            var auth = Field(idClaims, "https://api.openai.com/auth");
            var account = Text(tokens, "account_id");
            if (account.Length == 0) account = Text(auth, "chatgpt_account_id");
            if (account.Length == 0) throw new JsonException("Codex login account identity missing");
            return new(provider, "Codex 本机", access, account, account, Text(auth, "chatgpt_plan_type"), Expired(Claims(access), "exp"));
        }
        var oauth = Field(root, "claudeAiOauth");
        var accessToken = Text(oauth, "accessToken");
        if (accessToken.Length == 0) return null;
        var identity = Text(oauth, "accountUuid");
        if (identity.Length == 0) identity = Text(Claims(accessToken), "sub");
        // Separate metadata files can lag an account switch; never attach their identity to this token.
        // An opaque token without an identity claim still isolates each login. It is never exposed.
        if (identity.Length == 0) identity = "token:" + accessToken;
        var subscription = Text(oauth, "subscriptionType");
        var tier = Text(oauth, "rateLimitTier");
        if (subscription == "max" && tier == "default_claude_max_5x") subscription = "max_5x";
        if (subscription == "max" && tier == "default_claude_max_20x") subscription = "max_20x";
        return new(provider, "Claude Code 本机", accessToken, identity, "", subscription,
            Expired(oauth, "expiresAt", milliseconds: true) || Expired(Claims(accessToken), "exp"));
    }

    private static async Task<bool> UsesCodexFileStore(string directory, CancellationToken token)
    {
        var path = Path.Combine(directory, "config.toml");
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        using (file)
        {
            if (file.Length > MaximumBytes) throw new UnsupportedStoreException();
            using var reader = new StreamReader(file);
            string? multiline = null;
            string? selected = null;
            while (await reader.ReadLineAsync(token) is { } line)
            {
                var trimmed = line.TrimStart();
                if (multiline is not null)
                {
                    if (line.Contains(multiline, StringComparison.Ordinal)) multiline = null;
                    continue;
                }
                if (trimmed.StartsWith('#') || trimmed.Length == 0) continue;
                // TOML bare root keys end at the first table; similarly named profile keys are unrelated.
                if (trimmed.StartsWith('[')) break;
                var match = Regex.Match(line, "^\\s*(?:cli_auth_credentials_store|\"cli_auth_credentials_store\"|'cli_auth_credentials_store')\\s*=\\s*(?<value>.*)$", RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    if (selected is not null) return false;
                    var value = Regex.Match(match.Groups["value"].Value, "^(?:\"(?<mode>[^\"]*)\"|'(?<mode>[^']*)')\\s*(?:#.*)?$", RegexOptions.CultureInvariant);
                    if (!value.Success) return false;
                    selected = value.Groups["mode"].Value;
                    continue;
                }
                if (!TrackTomlStrings(line, ref multiline)) return false;
            }
            return multiline is null && (selected is null or "file");
        }
    }

    private static bool TrackTomlStrings(string line, ref string? multiline)
    {
        // Ignore delimiter-like text in ordinary quoted strings and comments.
        for (var i = 0; i < line.Length; i++)
        {
            var quote = line[i];
            if (quote == '#') break;
            if (quote is not ('\'' or '"')) continue;
            var triple = i + 2 < line.Length && line[i + 1] == quote && line[i + 2] == quote;
            var delimiter = new string(quote, triple ? 3 : 1);
            var closed = false;
            for (i += delimiter.Length; i < line.Length; i++)
            {
                if (quote == '"' && line[i] == '\\') { i++; continue; }
                if (line.AsSpan(i).StartsWith(delimiter, StringComparison.Ordinal))
                { i += delimiter.Length - 1; closed = true; break; }
            }
            if (!closed)
            {
                if (!triple) return false;
                multiline = delimiter;
                return true;
            }
        }
        return true;
    }

    private static QuotaAccount Account(Login login, QuotaWindow[]? windows = null) => new(
        "local:" + login.Provider + ":" + Hash(login.Identity), login.Alias, login.Provider,
        QuotaAdapter.PlanName(login.Provider, login.Plan), windows ?? []);

    private async Task<Result> ReadProvider(string provider, CancellationToken token)
    {
        var alias = provider == "codex" ? "Codex 本机" : "Claude Code 本机";
        Login? login = null;
        try
        {
            login = await ReadLogin(provider, token);
            if (login is null) return new("unconfigured", alias + "：未找到订阅 OAuth 登录，API Key 登录不提供套餐额度");
            if (login.Expired) return new("unauthorized", alias + "：登录已过期，请在原客户端重新登录", Account(login));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var url = provider == "codex" ? CodexUsageUrl : ClaudeQuotaAdapter.UsageUrl;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (provider == "codex")
            {
                request.Headers.Add("Chatgpt-Account-Id", login.AccountId);
                request.Headers.UserAgent.ParseAdd("codex-tui/0.149.1");
            }
            else request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new("unauthorized", alias + "：登录不可用或查询权限不足，请在原客户端检查登录", Account(login));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new("offline", alias + "：额度查询被限流，请稍后重试", Account(login));
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is { } finalUri && finalUri != request.RequestUri)
                return new("offline", alias + "：额度服务暂不可用", Account(login));
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new JsonException("Usage response exceeds size limit");
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var payload = await ReadJson(body, timeout.Token);
            var currentLogin = await ReadLogin(provider, timeout.Token);
            if (currentLogin is null || currentLogin.Identity != login.Identity || currentLogin.Token != login.Token)
                return new("unconfigured", alias + "：登录已切换，等待重新读取");
            var observed = DateTimeOffset.UtcNow;
            var windows = provider == "codex" ? UpstreamQuotaAdapter.Windows(payload, observed) : ClaudeQuotaAdapter.Windows(payload, observed, login.Plan);
            // Prefer the current authenticated usage response over potentially old JWT plan metadata.
            foreach (var window in windows)
                if (!window.TryGetValue("plan_type", out var plan) || plan is not string { Length: > 0 }) window["plan_type"] = login.Plan;
            var account = Account(login);
            var selection = new AccountSelection(account.Id, account.Alias, provider, "", "", "");
            var snapshot = JsonSerializer.SerializeToElement(new { items = new[] { new { row_key = account.Id, provider, windows } } });
            var parsed = QuotaAdapter.Parse(snapshot, [selection], observed).Single();
            return new("ok", alias + "：已读取本地登录套餐和额度", parsed.Plan.Length == 0 ? parsed with { Plan = account.Plan } : parsed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (UnsupportedStoreException) { return new("unconfigured", alias + "：当前凭据存储不是文件模式，暂不支持读取系统凭据库"); }
        catch (FileNotFoundException) { return new("unconfigured", alias + "：未找到本地 OAuth 登录"); }
        catch (DirectoryNotFoundException) { return new("unconfigured", alias + "：未找到本地 OAuth 登录"); }
        catch (UnauthorizedAccessException) { return new("unconfigured", alias + "：当前用户无法读取登录文件"); }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException)
        { return new("offline", alias + (login is null ? "：登录文件格式不可读取" : "：额度响应格式不可读取"), login is null ? null : Account(login)); }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        { return new("offline", alias + "：额度服务暂不可用", login is null ? null : Account(login)); }
    }
}
