using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WallpaperBridge;

// Match CPAMP v1.14.9. Older client identifiers receive an incomplete inventory.
public static class AntigravityQuotaAdapter
{
    public const string UserAgent = "antigravity/hub/2.23.0 darwin/arm64";
    public static readonly string[] SummaryUrls = [
        "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
        "https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:retrieveUserQuotaSummary",
        "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"
    ];
    public static readonly string[] ModelUrls = SummaryUrls.Select(url => url.Replace("retrieveUserQuotaSummary", "fetchAvailableModels", StringComparison.Ordinal)).ToArray();
    private static string Text(JsonElement node, string camel, string snake) => QuotaAdapter.Text(node, camel, QuotaAdapter.Text(node, snake)).Trim();
    public static string Project(JsonElement file)
    {
        var project = Text(file, "projectId", "project_id");
        foreach (var key in new[] { "metadata", "attributes" })
            if (project.Length == 0 && file.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Object)
                project = Text(nested, "projectId", "project_id");
        if (project.Length == 0 && file.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
            project = QuotaAdapter.Text(attributes, "gemini_virtual_project").Trim();
        if (project.Length == 0) throw new InvalidOperationException("Missing Antigravity project");
        return project;
    }
    public static object Request(AccountSelection account, string project, string url) => new {
        authIndex = account.AuthIndex, method = "POST", url,
        header = new Dictionary<string, string> { ["Authorization"] = "Bearer $TOKEN$", ["Content-Type"] = "application/json",
            ["User-Agent"] = UserAgent },
        data = JsonSerializer.Serialize(new { project })
    };
    public static Dictionary<string, object?>[] Windows(JsonElement payload, DateTimeOffset observed)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing Antigravity quota groups");
        var result = new List<Dictionary<string, object?>>();
        var identities = new HashSet<string>();
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid quota group");
            var label = Text(group, "displayName", "display_name");
            var groupId = Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (groupId.Length == 0 || !group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                throw new JsonException("Missing quota bucket identity");
            var scope = label.Contains("gemini", StringComparison.OrdinalIgnoreCase) ? "gemini"
                : Regex.IsMatch(label, "claude|gpt|external", RegexOptions.IgnoreCase) ? "claude_gpt" : "";
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid quota bucket");
                var fraction = QuotaAdapter.Number(bucket, "remainingFraction") ?? QuotaAdapter.Number(bucket, "remaining_fraction");
                var window = QuotaAdapter.Text(bucket, "window").Trim().ToLowerInvariant().Replace('_', '-');
                var bucketId = Text(bucket, "bucketId", "bucket_id");
                if (bucketId.Length == 0 && window.Length > 0) bucketId = groupId + "-" + window;
                var id = groupId + ":" + bucketId;
                if (fraction is null || bucketId.Length == 0 || !identities.Add(id)) throw new JsonException("Invalid or duplicate quota bucket");
                long? duration = window switch { "5h" or "5-hour" or "five-hour" => 18000, "24h" or "daily" or "day" => 86400, "7d" or "weekly" or "week" => 604800, _ => null };
                var resetText = Text(bucket, "resetTime", "reset_time");
                DateTimeOffset? reset = DateTimeOffset.TryParse(resetText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
                var item = new Dictionary<string, object?> {
                    ["provider_window_id"] = id, ["window_kind"] = duration == 18000 ? "five_hour" : duration == 604800 ? "weekly" : duration == 86400 ? "daily" : "unknown",
                    ["window_mode"] = scope.Length > 0 && duration is not null ? "fixed" : "unknown",
                    ["model_scope_kind"] = scope.Length > 0 ? "family" : "feature", ["model_scope_key"] = scope.Length > 0 ? scope : "__incomplete__",
                    ["source"] = "api_query", ["observed_at_ms"] = observed.ToUnixTimeMilliseconds(),
                    ["remaining_percent"] = Math.Clamp(fraction.Value * 100, 0, 100), ["used_percent"] = Math.Clamp((1 - fraction.Value) * 100, 0, 100),
                    ["boundary_accuracy"] = scope.Length > 0 && reset is not null ? "exact" : "unknown"
                };
                if (duration is not null) item["duration_seconds"] = duration;
                if (reset is not null) item["cycle_end_ms"] = reset.Value.ToUnixTimeMilliseconds();
                result.Add(item);
            }
        }
        if (result.Count == 0) throw new JsonException("No readable Antigravity quota windows");
        // Preserve the same provider IDs and scope keys used by existing CPAMP snapshots.
        foreach (var family in result.GroupBy(w => (string)w["model_scope_key"]!))
        {
            var weekly = family.FirstOrDefault(w => (string)w["window_kind"]! == "weekly");
            if (weekly is not null)
                foreach (var shortWindow in family.Where(w => (string)w["window_kind"]! == "five_hour"))
                { shortWindow["relationship_kind"] = "concurrent_subwindow"; shortWindow["container_provider_window_id"] = weekly["provider_window_id"]; }
        }
        return result.OrderBy(w => (long?)w.GetValueOrDefault("duration_seconds") ?? long.MaxValue).ThenBy(w => (string)w["provider_window_id"]!, StringComparer.Ordinal).ToArray();
    }

    // Some accounts expose only Claude/GPT in quota-summary. Their Gemini balance
    // lives in fetchAvailableModels and has no declared short/weekly duration.
    public static Dictionary<string, object?>[] ModelWindows(JsonElement payload, DateTimeOffset observed)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object)
            throw new JsonException("Missing Antigravity model quotas");
        var tabs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in new[] { "tabModelIds", "tab_model_ids" })
            if (payload.TryGetProperty(key, out var ids) && ids.ValueKind == JsonValueKind.Array)
                foreach (var id in ids.EnumerateArray()) if (id.ValueKind == JsonValueKind.String) tabs.Add(id.GetString()!);
        var entries = new List<(string Scope, double Fraction, DateTimeOffset? Reset)>();
        foreach (var model in models.EnumerateObject())
        {
            var value = model.Value;
            if (value.ValueKind != JsonValueKind.Object || tabs.Contains(model.Name)) continue;
            var search = model.Name + " " + string.Join(" ", new[] { "displayName", "display_name", "model", "apiProvider", "api_provider", "modelProvider", "model_provider" }.Select(key => QuotaAdapter.Text(value, key)));
            var scope = Regex.IsMatch(search, "gemini", RegexOptions.IgnoreCase) ? "gemini"
                : Regex.IsMatch(search, "claude|gpt|anthropic|openai", RegexOptions.IgnoreCase) ? "claude_gpt" : "";
            if (scope.Length == 0 || !(value.TryGetProperty("quotaInfo", out var quota) || value.TryGetProperty("quota_info", out quota)) || quota.ValueKind != JsonValueKind.Object) continue;
            double? fraction = null;
            foreach (var key in new[] { "remainingFraction", "remaining_fraction", "remaining" })
            {
                fraction = QuotaAdapter.Number(quota, key);
                if (fraction is null && double.TryParse(QuotaAdapter.Text(quota, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) fraction = number;
                if (fraction is not null) break;
            }
            // Missing quota is unknown, even when a reset timestamp is present.
            if (fraction is null) continue;
            DateTimeOffset? reset = DateTimeOffset.TryParse(Text(quota, "resetTime", "reset_time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
            entries.Add((scope, Math.Clamp(fraction.Value, 0, 1), reset));
        }
        var result = new List<Dictionary<string, object?>>();
        foreach (var family in entries.GroupBy(entry => entry.Scope))
        {
            var fraction = family.Min(entry => entry.Fraction);
            var limiting = family.Where(entry => entry.Fraction == fraction).ToArray();
            var reset = limiting.All(entry => entry.Reset is not null) ? limiting.Max(entry => entry.Reset) : null;
            var id = family.Key == "gemini" ? "gemini" : "claude-gpt";
            var window = new Dictionary<string, object?> {
                ["provider_window_id"] = id + ":" + id + "-shared", ["window_kind"] = "unknown", ["window_mode"] = "unknown",
                ["model_scope_kind"] = "family", ["model_scope_key"] = family.Key, ["source"] = "api_query",
                ["observed_at_ms"] = observed.ToUnixTimeMilliseconds(), ["remaining_percent"] = fraction * 100,
                ["used_percent"] = (1 - fraction) * 100, ["boundary_accuracy"] = reset is not null ? "exact" : "unknown"
            };
            if (reset is not null) window["cycle_end_ms"] = reset.Value.ToUnixTimeMilliseconds();
            result.Add(window);
        }
        if (result.Count == 0) throw new JsonException("No readable Antigravity model quotas");
        return result.ToArray();
    }
}
