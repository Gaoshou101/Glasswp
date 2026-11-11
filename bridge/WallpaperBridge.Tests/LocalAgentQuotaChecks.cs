using System.Net;
using System.Text;
using System.Text.Json;
using WallpaperBridge;

static class LocalAgentQuotaChecks
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static string Jwt(object claims) => "header." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public static async Task Run(Action<bool, string> check)
    {
        var home = Path.Combine(Path.GetTempPath(), "wallpaper-local-agents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        var codexPath = Path.Combine(home, ".codex", "auth.json");
        var claudePath = Path.Combine(home, ".claude", ".credentials.json");
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var reset = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var identityToken = Jwt(new Dictionary<string, object?> {
            ["email"] = "private@example.invalid",
            ["https://api.openai.com/auth"] = new { chatgpt_account_id = "private-account", chatgpt_plan_type = "plus" }
        });
        var codexToken = Jwt(new { exp = future, secret = "private-access" });
        object CodexLogin(string account = "private-account", string? access = null) => new {
            auth_mode = "chatgpt", OPENAI_API_KEY = (string?)null,
            tokens = new { id_token = identityToken, access_token = access ?? codexToken, refresh_token = "never-use-this-refresh", account_id = account }
        };
        object ClaudeLogin(long? expires = null, string access = "private-claude-access", string? account = "private-claude-account") => new {
            claudeAiOauth = new { accessToken = access, refreshToken = "never-use-claude-refresh", expiresAt = expires ?? future * 1000,
                subscriptionType = "max", rateLimitTier = "default_claude_max_20x", accountUuid = account }
        };
        var codexUsage = new { plan_type = "pro", rate_limit = new {
            primary_window = new { limit_window_seconds = 18000, used_percent = 100, reset_at = reset },
            secondary_window = new { limit_window_seconds = 604800, used_percent = 35, reset_at = reset + 604800 }
        }, rate_limit_reset_credits = new { available_count = 2 } };
        var claudeUsage = new { five_hour = new { utilization = 25, resets_at = DateTimeOffset.FromUnixTimeSeconds(reset).ToString("O") } };
        try
        {
            var calls = 0;
            using var client = new HttpClient(new Handler((request, _) => {
                calls++;
                check(request.Method == HttpMethod.Get && request.RequestUri?.Scheme == "https", "local agents only perform HTTPS quota GET requests");
                check(request.RequestUri?.Host is "chatgpt.com" or "api.anthropic.com", "local OAuth credentials only target fixed provider hosts");
                check(request.Content is null, "local agent reader never sends prompt or credential-refresh bodies");
                if (request.RequestUri!.Host == "chatgpt.com")
                {
                    check(request.RequestUri.AbsolutePath == "/backend-api/wham/usage" && request.Headers.Authorization?.Parameter == codexToken
                        && request.Headers.GetValues("Chatgpt-Account-Id").Single() == "private-account", "Codex local login selects its own usage account");
                    return Task.FromResult(Json(codexUsage));
                }
                check(request.RequestUri.AbsolutePath == "/api/oauth/usage" && request.Headers.Authorization?.Parameter == "private-claude-access"
                    && request.Headers.GetValues("anthropic-beta").Single() == "oauth-2025-04-20", "Claude local login selects the OAuth usage endpoint");
                return Task.FromResult(Json(claudeUsage));
            }));
            var reader = new LocalAgentQuotaReader(client, home);
            var disabled = await reader.Read(false, false, CancellationToken.None);
            check(disabled.Status == "unconfigured" && calls == 0, "disabled local agents do not access the network");
            var missing = await reader.Read(true, true, CancellationToken.None);
            check(missing.Status == "unconfigured" && missing.Accounts.Length == 0 && calls == 0, "missing local logins require no CPAMP configuration or network request");
            await File.WriteAllTextAsync(codexPath, JsonSerializer.Serialize(CodexLogin()));
            await File.WriteAllTextAsync(claudePath, JsonSerializer.Serialize(ClaudeLogin()));
            var both = await reader.Read(true, true, CancellationToken.None);
            check(both.Status == "ok" && both.Accounts.Length == 2 && calls == 2, "Codex and Claude local quotas work independently of CPAMP");
            var codex = both.Accounts.Single(a => a.Provider == "codex");
            var claude = both.Accounts.Single(a => a.Provider == "claude");
            check(codex.Id.StartsWith("local:codex:") && codex.Id.Length == "local:codex:".Length + 64 && codex.Alias == "Codex 本机", "local Codex account identity is hashed and alias reveals no email");
            check(codex.Plan == "GPT Pro 20x" && codex.Windows[0].RemainingPercent == 0 && !codex.Windows[0].Stale
                && codex.ResetCredits?.Available == 2, "local Codex uses current server plan and canonical quota parsing, exhaustion and reset credits");
            check(claude.Plan == "Claude Max 20x" && claude.Windows.Single().RemainingPercent == 75 && claude.Alias == "Claude Code 本机", "local Claude login tier and quota share the existing canonical adapter");
            var serialized = JsonSerializer.Serialize(both);
            check(!serialized.Contains("private-") && !serialized.Contains("private@example.invalid") && !serialized.Contains("never-use")
                && !serialized.Contains(codexToken), "local snapshots contain no tokens, raw account IDs or login emails");
            check((await File.ReadAllTextAsync(codexPath)) == JsonSerializer.Serialize(CodexLogin())
                && (await File.ReadAllTextAsync(claudePath)) == JsonSerializer.Serialize(ClaudeLogin()), "local quota reads never rewrite client credential files");
            File.Delete(claudePath);
            using (var onlyCodexClient = new HttpClient(new Handler((_, _) => Task.FromResult(Json(codexUsage)))))
                check((await new LocalAgentQuotaReader(onlyCodexClient, home).Read(true, true, CancellationToken.None)).Status == "ok",
                    "absent Claude login does not degrade a healthy local Codex quota");

            await File.WriteAllTextAsync(claudePath, JsonSerializer.Serialize(ClaudeLogin(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds())));
            var partial = await reader.Read(true, true, CancellationToken.None);
            check(partial.Status == "stale" && partial.Accounts.Single(a => a.Provider == "claude").Windows.Length == 0 && calls == 3,
                "expired Claude login is explicit and never reuses another login's quota");
            await File.WriteAllTextAsync(codexPath, JsonSerializer.Serialize(CodexLogin(access: Jwt(new { exp = 1 }))));
            var expired = await reader.Read(true, false, CancellationToken.None);
            check(expired.Status == "unauthorized" && expired.Accounts.Single().Plan == "GPT Plus" && expired.Accounts.Single().Windows.Length == 0 && calls == 3,
                "expired Codex access token retains only known plan metadata and never refreshes tokens");
            await File.WriteAllTextAsync(codexPath, "{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"private-key\",\"tokens\":{\"access_token\":\"stale-oauth\"}}");
            check((await reader.Read(true, false, CancellationToken.None)).Status == "unconfigured" && calls == 3,
                "API key authentication never masquerades as a subscription login");
            foreach (var mode in new[] { "chatgptAuthTokens", "agentIdentity", "personalAccessToken", "unknown" })
            {
                await File.WriteAllTextAsync(codexPath, JsonSerializer.Serialize(new { auth_mode = mode, tokens = new { access_token = codexToken, account_id = "private-account" } }));
                check((await reader.Read(true, false, CancellationToken.None)).Status == "unconfigured" && calls == 3,
                    "unsupported Codex authentication mode cannot reuse OAuth token-shaped fields: " + mode);
            }
            var configPath = Path.Combine(home, ".codex", "config.toml");
            await File.WriteAllTextAsync(codexPath, JsonSerializer.Serialize(CodexLogin()));
            foreach (var mode in new[] { "keyring", "auto", "ephemeral", "unknown" })
            {
                await File.WriteAllTextAsync(configPath, "cli_auth_credentials_store = \"" + mode + "\"\n");
                var unsupported = await reader.Read(true, false, CancellationToken.None);
                check(unsupported.Status == "unconfigured" && unsupported.Accounts.Length == 0 && calls == 3,
                    "non-file Codex credential store never reads an old auth.json fallback: " + mode);
            }
            await File.WriteAllTextAsync(configPath, "notes = '\"\"\"'\ncli_auth_credentials_store = 'keyring'\n");
            check((await reader.Read(true, false, CancellationToken.None)).Status == "unconfigured" && calls == 3,
                "triple quote text inside a TOML literal cannot hide a real keyring setting");
            foreach (var config in new[] {
                "cli_auth_credentials_store = 'file' # supported\n[profiles.somewhere]\ncli_auth_credentials_store = 'keyring'\n",
                "[profiles.somewhere]\ncli_auth_credentials_store = 'keyring'\n",
                "notes = '''\ncli_auth_credentials_store = 'keyring'\n'''\ncli_auth_credentials_store = 'file'\n"
            })
            {
                await File.WriteAllTextAsync(configPath, config);
                using var storeClient = new HttpClient(new Handler((_, _) => Task.FromResult(Json(codexUsage))));
                check((await new LocalAgentQuotaReader(storeClient, home).Read(true, false, CancellationToken.None)).Status == "ok",
                    "Codex credential storage selection reads only an actual root TOML key");
            }
            File.Delete(configPath);
            await File.WriteAllTextAsync(codexPath, "{broken-private-content");
            var malformed = await reader.Read(true, false, CancellationToken.None);
            check(malformed.Accounts.Length == 0 && !malformed.Message.Contains("private-content"), "malformed login file errors cannot expose credential contents");

            await File.WriteAllTextAsync(codexPath, JsonSerializer.Serialize(CodexLogin()));
            using var rejectedClient = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { secret = codexToken }, HttpStatusCode.Unauthorized))));
            var rejected = await new LocalAgentQuotaReader(rejectedClient, home).Read(true, false, CancellationToken.None);
            check(rejected.Status == "unauthorized" && rejected.Accounts.Single().Windows.Length == 0 && !rejected.Message.Contains(codexToken),
                "provider authentication errors expose neither provider response nor previous quota");
            using var malformedClient = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { private_payload = codexToken }))));
            var noWindows = await new LocalAgentQuotaReader(malformedClient, home).Read(true, false, CancellationToken.None);
            check(noWindows.Status == "offline" && noWindows.Accounts.Single().Plan == "GPT Plus" && noWindows.Accounts.Single().Windows.Length == 0,
                "unreadable quota response is a failure instead of an invented zero or free plan");
            using var noPlanClient = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { rate_limit = codexUsage.rate_limit }))));
            var noPlan = await new LocalAgentQuotaReader(noPlanClient, home).Read(true, false, CancellationToken.None);
            check(noPlan.Status == "ok" && noPlan.Accounts.Single().Plan == "GPT Plus", "quota responses without a plan preserve explicit local login plan metadata");
            using var switchClient = new HttpClient(new Handler(async (_, _) => {
                await File.WriteAllTextAsync(codexPath, JsonSerializer.Serialize(CodexLogin("other-account")));
                return Json(codexUsage);
            }));
            var switched = await new LocalAgentQuotaReader(switchClient, home).Read(true, false, CancellationToken.None);
            check(switched.Accounts.Length == 0 && switched.Status == "unconfigured", "a login switch while fetching discards the previous account's response");

            await File.WriteAllTextAsync(claudePath, JsonSerializer.Serialize(ClaudeLogin(account: null)));
            using var claudeClient = new HttpClient(new Handler((_, _) => Task.FromResult(Json(claudeUsage))));
            var first = await new LocalAgentQuotaReader(claudeClient, home).Read(false, true, CancellationToken.None);
            await File.WriteAllTextAsync(claudePath, JsonSerializer.Serialize(ClaudeLogin(access: "different-local-login", account: null)));
            var second = await new LocalAgentQuotaReader(claudeClient, home).Read(false, true, CancellationToken.None);
            check(first.Accounts.Single().Id != second.Accounts.Single().Id, "opaque Claude logins without account metadata stay isolated across token changes");

            await File.WriteAllTextAsync(codexPath, new string('x', 1024 * 1024 + 1));
            var oversized = await reader.Read(true, false, CancellationToken.None);
            check(oversized.Status == "offline" && oversized.Accounts.Length == 0 && calls == 3, "oversized credential documents are rejected before network access");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await reader.Read(true, false, cancelled.Token); check(false, "local reader cancellation must propagate"); }
            catch (OperationCanceledException) { check(true, "local reader cancellation propagates without converting shutdown into an offline result"); }
            await File.WriteAllTextAsync(codexPath, JsonSerializer.Serialize(CodexLogin()));
            await File.WriteAllTextAsync(claudePath, JsonSerializer.Serialize(ClaudeLogin()));
            var previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            var previousClaudeDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            try
            {
                Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(home, ".codex"));
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(home, ".claude"));
                var overridden = await new LocalAgentQuotaReader(client).Read(true, true, CancellationToken.None);
                check(overridden.Status == "ok" && overridden.Accounts.Length == 2, "real-profile discovery respects CODEX_HOME and CLAUDE_CONFIG_DIR overrides");
                Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(home, "missing-profile"));
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(home, "missing-profile"));
                check((await new LocalAgentQuotaReader(client, home).Read(true, true, CancellationToken.None)).Status == "ok",
                    "explicit isolated profile never reads the process's credential directory overrides");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousClaudeDirectory);
            }
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    // Explicitly invoked smoke check; performs one free quota GET and prints no identity or credential data.
    public static async Task RunLiveCodex()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler);
        var data = await new LocalAgentQuotaReader(http).Read(true, false, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new {
            check = "local-codex-readonly", data.Status,
            accounts = data.Accounts.Select(account => new { account.Plan, windows = account.Windows.Length })
        }));
    }
}
