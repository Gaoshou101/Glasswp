using System.Text.Json;
using WallpaperBridge;

static class LocalAntigravityCliQuotaChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var calls = 0; string? workingDirectory = null;
        var report = JsonSerializer.Serialize(new { status = "SUCCESS", command = new { name = "usage", data = new {
            groups = new[] { new { displayName = "Gemini Models", buckets = new object[] {
                new { bucketId = "daily", window = "5h", remaining = new { @case = "remainingFraction", value = 0.75 }, resetTime = "2099-01-01T00:00:00Z" },
                new { bucketId = "disabled", disabled = true, remainingFraction = 1 }
            } } }
        } } });
        var reader = new LocalAntigravityCliQuotaReader(userHome: "isolated-profile", executable: "E:\\fixture\\agy.exe", command: async (info, token) => {
            calls++; workingDirectory = info.WorkingDirectory;
            check(info.CreateNoWindow && !info.UseShellExecute && info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError,
                "Antigravity usage process has no visible window or interactive input");
            if (info.ArgumentList.SequenceEqual(new[] { "--version" })) return new(0, "1.1.11", "");
            check(info.ArgumentList.SequenceEqual(new[] { "-p", "/usage", "--output-format", "json", "--agent", "wallpaper-usage", "--print-timeout", "15s" }),
                "Antigravity invokes the dedicated usage command with fixed arguments");
            var agent = await File.ReadAllTextAsync(Path.Combine(info.WorkingDirectory, ".agents", "agents", "wallpaper-usage", "agent.md"), token);
            check(agent.Contains("tools: []") && agent.Contains("mcpServers: []") && File.Exists(info.Environment["BROWSER"]), "Antigravity usage uses an isolated no-tools agent and disabled browser launcher");
            return new(0, report, "");
        });
        var result = await reader.Read(CancellationToken.None);
        check(result.Status == "ok" && calls == 2 && result.Accounts.Single().Plan == "" && result.Accounts.Single().Windows.Single().RemainingPercent == 75,
            "Antigravity command report yields quota without inventing account or plan evidence");
        check(workingDirectory is not null && !Directory.Exists(workingDirectory), "Antigravity isolated temporary agent is cleaned up after the command");
        using (var sensitive = JsonDocument.Parse("""
            {"groups":[{"displayName":"Gemini private@example.invalid","buckets":[{"bucketId":"Bearer-private-account-secret","window":"5h","remainingFraction":0.5}]},
            {"displayName":"private-account-secret","buckets":[{"bucketId":"sk-private-token-value","remainingFraction":0.75}]}]}
            """))
        {
            var sanitized = LocalAntigravityCliQuotaReader.ParseWindows(sensitive.RootElement, DateTimeOffset.UtcNow);
            var dto = JsonSerializer.Serialize(sanitized);
            check(sanitized.All(window => window.Id.StartsWith("local:antigravity-window:") && window.Id.Length == "local:antigravity-window:".Length + 64)
                && sanitized.Any(window => window.Label == "Gemini · 5 小时") && sanitized.Any(window => window.Label == "Antigravity · 额度")
                && !dto.Contains("private") && !dto.Contains("Bearer") && !dto.Contains("@"),
                "Antigravity native quota hashes bucket identities and uses only recognized safe family labels");
        }
        var old = new LocalAntigravityCliQuotaReader(userHome: "isolated", executable: "E:\\fixture\\agy.exe", command: (_, _) => Task.FromResult(new LocalAntigravityCliQuotaReader.CommandResult(0, "1.1.10", "")));
        check((await old.Read(CancellationToken.None)).Status == "offline", "unverified older Antigravity CLI versions never receive a model prompt");
        var denied = new LocalAntigravityCliQuotaReader(userHome: "isolated", executable: "E:\\fixture\\agy.exe", command: (info, _) => Task.FromResult(new LocalAntigravityCliQuotaReader.CommandResult(0,
            info.ArgumentList.Contains("--version") ? "1.1.11" : "{\"status\":\"FAILURE\",\"error\":\"private@example.invalid token-secret\"}", "")));
        var failure = await denied.Read(CancellationToken.None);
        check(failure.Status == "unauthorized" && failure.Accounts.Length == 0 && !failure.Message.Contains("private") && !failure.Message.Contains("token-secret"), "Antigravity command failures never expose CLI credential or account output");
        using (var empty = JsonDocument.Parse("{\"groups\":[]}"))
        {
            try { LocalAntigravityCliQuotaReader.ParseWindows(empty.RootElement, DateTimeOffset.UtcNow); check(false, "empty Antigravity report must fail"); }
            catch (JsonException) { check(true, "empty Antigravity report cannot advertise a healthy quota account"); }
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await reader.Read(cancelled.Token); check(false, "Antigravity shutdown cancellation must propagate"); }
        catch (OperationCanceledException) { check(true, "Antigravity shutdown cancellation is propagated"); }
    }
}
