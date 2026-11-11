using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WallpaperBridge;

// Mirrors @magpie-community/agy-cli's dedicated /usage command. No general model prompt is issued.
// The original CLI owns authentication and may refresh its own login normally.
internal sealed class LocalAntigravityCliQuotaReader(string? userHome = null,
    Func<ProcessStartInfo, CancellationToken, Task<LocalAntigravityCliQuotaReader.CommandResult>>? command = null,
    string? executable = null)
{
    internal sealed record CommandResult(int ExitCode, string Output, string Error);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private string? Resolve()
    {
        if (executable is not null) return executable;
        // Isolated test/profile reads must never run a user's installed CLI or keyring.
        if (userHome is not null) return null;
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(directory => directory.Trim('"')).Concat(new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Antigravity", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "bin")
        });
        foreach (var directory in directories)
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, "agy.exe"); if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
    public async Task<QuotaData> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var file = Resolve();
        if (file is null) return new("unconfigured", null, "Antigravity CLI：未发现 agy", []);
        var root = Path.Combine(Path.GetTempPath(), "wallpaper-agy-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var agent = Path.Combine(root, ".agents", "agents", "wallpaper-usage"); Directory.CreateDirectory(agent);
            await File.WriteAllTextAsync(Path.Combine(agent, "agent.md"), "---\nname: wallpaper-usage\ndescription: Read CLI usage only\nmainAgent: true\nsubagent: false\ntools: []\nmcpServers: []\n---\nDo not run model prompts or tools.\n", token);
            await File.WriteAllTextAsync(Path.Combine(root, "open.cmd"), "@exit /b 1\r\n", token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(18));
            var run = command ?? Run;
            var versionInfo = Start(file, root, ["--version"]);
            var version = await run(versionInfo, timeout.Token);
            if (version.ExitCode != 0 || !Version.TryParse(version.Output.Trim(), out var parsed) || parsed < new Version(1, 1, 11))
                return new("offline", null, "Antigravity CLI：用量查询需要 agy 1.1.11 或更高版本", []);
            var info = Start(file, root, ["-p", "/usage", "--output-format", "json", "--agent", "wallpaper-usage", "--print-timeout", "15s"]);
            var output = await run(info, timeout.Token);
            if (output.ExitCode != 0) return new("offline", null, "Antigravity CLI：用量查询失败，请在原客户端检查登录", []);
            using var document = JsonDocument.Parse(output.Output, new JsonDocumentOptions { MaxDepth = 32 });
            var report = document.RootElement;
            if (Text(report, "status") != "SUCCESS" || Text(Field(report, "command"), "name") != "usage")
                return new("unauthorized", null, "Antigravity CLI：未返回成功的用量报告，请在原客户端检查登录", []);
            var observed = DateTimeOffset.UtcNow;
            var windows = ParseWindows(Field(Field(report, "command"), "data"), observed);
            // /usage exposes no verified account or subscription tier. This is a transient profile slot, never a cached identity.
            var slot = Path.GetFullPath(file) + "\n" + (userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return new("ok", observed, "Antigravity CLI：已读取实时额度，登录由原客户端管理", [new("local:antigravity:" + Hash(slot), "Antigravity CLI 本机", "antigravity", "", windows)]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or ArgumentException)
        { return new("offline", null, "Antigravity CLI：实时额度暂不可用", []); }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    private static string Text(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
    private static JsonElement Field(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) ? value : default;
    private static ProcessStartInfo Start(string file, string root, string[] args)
    {
        var info = new ProcessStartInfo { FileName = file, WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["BROWSER"] = Path.Combine(root, "open.cmd"); info.Environment["CI"] = "1";
        info.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
        info.Environment["NO_COLOR"] = "1"; info.Environment["PATH"] = root + Path.PathSeparator + info.Environment["PATH"];
        return info;
    }
    private static async Task<CommandResult> Run(ProcessStartInfo info, CancellationToken token)
    {
        using var process = new Process { StartInfo = info }; process.Start(); process.StandardInput.Close();
        using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var total = 0;
        async Task<string> Read(StreamReader reader)
        {
            var buffer = new char[4096]; var output = new StringBuilder(); int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                if (Interlocked.Add(ref total, Encoding.UTF8.GetByteCount(buffer, 0, count)) > 1024 * 1024)
                { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw new IOException(); }
                output.Append(buffer, 0, count);
            }
            return output.ToString();
        }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        try { await process.WaitForExitAsync(token); await Task.WhenAll(stdout, stderr); return new(process.ExitCode, await stdout, await stderr); }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }
    internal static QuotaWindow[] ParseWindows(JsonElement data, DateTimeOffset observed)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new JsonException();
        var node = JsonNode.Parse(data.GetRawText())?.AsObject() ?? throw new JsonException();
        if (node["groups"] is not JsonArray groups) throw new JsonException();
        foreach (var group in groups.OfType<JsonObject>())
        {
            if (group["displayName"] is null) group["displayName"] = group["display_name"]?.DeepClone();
            if (group["buckets"] is not JsonArray buckets) continue;
            foreach (var bucket in buckets.OfType<JsonObject>().ToArray())
            {
                if (bucket["disabled"]?.GetValue<bool>() == true) { buckets.Remove(bucket); continue; }
                if (bucket["bucketId"] is null) bucket["bucketId"] = bucket["bucket_id"]?.DeepClone() ?? bucket["id"]?.DeepClone();
                if (bucket["remainingFraction"] is null) bucket["remainingFraction"] = bucket["remaining_fraction"]?.DeepClone();
                if (bucket["remainingFraction"] is null && bucket["remaining"] is JsonObject remaining)
                    bucket["remainingFraction"] = remaining["remainingFraction"]?.DeepClone() ?? remaining["remaining_fraction"]?.DeepClone()
                        ?? (remaining["case"]?.GetValue<string>() == "remainingFraction" ? remaining["value"]?.DeepClone() : null);
                if (bucket["remainingFraction"] is not JsonValue value || !value.TryGetValue<double>(out var fraction) || !double.IsFinite(fraction) || fraction < 0 || fraction > 1)
                    buckets.Remove(bucket);
            }
        }
        var normalized = JsonSerializer.SerializeToElement(node);
        var windows = AntigravityQuotaAdapter.Windows(normalized, observed);
        var selected = new AccountSelection("slot", "Antigravity CLI 本机", "antigravity", "", "", "");
        var parsed = QuotaAdapter.Parse(JsonSerializer.SerializeToElement(new { items = new[] { new { row_key = "slot", windows } } }), [selected], observed).Single().Windows;
        if (parsed.Length == 0) throw new JsonException();
        var scopes = windows.ToDictionary(window => (string)window["provider_window_id"]!, window => (string)window["model_scope_key"]!, StringComparer.Ordinal);
        return parsed.Select(window =>
        {
            var scope = scopes.GetValueOrDefault(window.Id);
            var family = scope == "claude_gpt" ? "Claude/GPT" : scope == "gemini"
                ? window.Id.StartsWith("gemini-4-argon:", StringComparison.OrdinalIgnoreCase) ? "Gemini 4 Argon" : "Gemini" : "Antigravity";
            var period = window.DurationSeconds switch { 18000 => "5 小时", 604800 => "周额度", 86400 => "日额度", _ => "额度" };
            return window with { Id = "local:antigravity-window:" + Hash(window.Id), Label = family + " · " + period };
        }).ToArray();
    }
}
