using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WallpaperBridge;

public static class UpstreamQuotaAdapter
{
    // Match the CPAMP v1.13.1 Codex usage GET. CPA supplies OAuth tokens by authIndex.
    public static object Request(AccountSelection a) => new {
        authIndex = a.AuthIndex, method = "GET", url = "https://chatgpt.com/backend-api/wham/usage",
        header = new Dictionary<string, string> { ["Authorization"] = "Bearer $TOKEN$", ["Chatgpt-Account-Id"] = a.AccountId,
            ["Content-Type"] = "application/json", ["User-Agent"] = "codex-tui/0.149.1" }
    };
    private static JsonElement Field(JsonElement node, string snake, string camel) => node.ValueKind == JsonValueKind.Object && (node.TryGetProperty(snake, out var value) || node.TryGetProperty(camel, out value)) ? value : default;
    private static double? Number(JsonElement node, string snake, string camel) {
        var v = Field(node, snake, camel);
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n
            : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && double.IsFinite(n) ? n : null;
    }
    public static Dictionary<string, object?>[] Windows(JsonElement payload, DateTimeOffset observed)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new JsonException("Missing usage inventory");
        var plan = QuotaAdapter.Text(payload, "plan_type", QuotaAdapter.Text(payload, "planType"));
        var windows = new List<Dictionary<string, object?>>();
        void AddFamily(JsonElement rate, string prefix, string scopeKind, string scopeKey, string[]? modelIds = null)
        {
            if (rate.ValueKind != JsonValueKind.Object) return;
            var family = new List<Dictionary<string, object?>>();
            foreach (var key in new[] { ("primary_window", "primaryWindow"), ("secondary_window", "secondaryWindow") })
            {
                var w = Field(rate, key.Item1, key.Item2); if (w.ValueKind != JsonValueKind.Object) continue;
                var duration = Number(w, "limit_window_seconds", "limitWindowSeconds");
                if (duration is <= 0) continue; // A zero-minute window is absent.
                var kind = duration == 18000 ? "five_hour" : duration == 604800 ? "weekly" : duration is >= 2419200 and <= 2678400 ? "monthly" : "unknown";
                var id = kind == "five_hour" ? "five-hour" : kind == "weekly" ? "weekly" : kind == "monthly" ? "monthly" : $"window-{duration?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}-{family.Count}";
                var used = Number(w, "used_percent", "usedPercent");
                var resetAt = Number(w, "reset_at", "resetAt");
                var resetAfter = Number(w, "reset_after_seconds", "resetAfterSeconds");
                DateTimeOffset? reset = null; var accuracy = "unknown";
                if (resetAt is > 0 and < 253402300800) { reset = DateTimeOffset.FromUnixTimeSeconds((long)resetAt); accuracy = "exact"; }
                else if (resetAfter is >= 0 and < 31536000) { reset = observed.AddSeconds(resetAfter.Value); accuracy = "derived"; }
                var item = new Dictionary<string, object?> {
                    ["provider_window_id"] = prefix.Length == 0 ? id : prefix == "code-review" ? prefix + "-" + id : prefix + "-" + id + "-0",
                    ["window_kind"] = kind, ["window_mode"] = "fixed", ["model_scope_kind"] = scopeKind,
                    ["model_scope_key"] = scopeKey, ["source"] = "api_query", ["observed_at_ms"] = observed.ToUnixTimeMilliseconds(),
                    ["boundary_accuracy"] = accuracy, ["plan_type"] = plan
                };
                if (modelIds is not null) item["model_ids"] = modelIds;
                if (duration is > 0 and < 31536000) item["duration_seconds"] = (long)duration;
                if (used is not null) { item["used_percent"] = Math.Clamp(used.Value, 0, 100); item["remaining_percent"] = Math.Clamp(100 - used.Value, 0, 100); }
                if (reset is not null) item["cycle_end_ms"] = reset.Value.ToUnixTimeMilliseconds();
                family.Add(item);
            }
            var container = family.FirstOrDefault(w => (string)w["window_kind"]! == "weekly") ?? family.FirstOrDefault(w => (string)w["window_kind"]! == "monthly");
            foreach (var w in family.Where(w => (string)w["window_kind"]! == "five_hour"))
                if (container is not null) { w["relationship_kind"] = "concurrent_subwindow"; w["container_provider_window_id"] = container["provider_window_id"]; }
            windows.AddRange(family);
        }
        AddFamily(Field(payload, "rate_limit", "rateLimit"), "", "family", "codex_main");
        var resetCredits = Field(payload, "rate_limit_reset_credits", "rateLimitResetCredits");
        var availableCredits = Number(resetCredits, "available_count", "availableCount");
        if (availableCredits is >= 0 and <= int.MaxValue && availableCredits == Math.Truncate(availableCredits.Value))
            foreach (var window in windows) window["reset_credits_available"] = (int)availableCredits.Value;
        AddFamily(Field(payload, "code_review_rate_limit", "codeReviewRateLimit"), "code-review", "feature", "code_review");
        var additional = Field(payload, "additional_rate_limits", "additionalRateLimits");
        if (additional.ValueKind == JsonValueKind.Array)
        {
            var prefixes = new HashSet<string>();
            foreach (var item in additional.EnumerateArray())
            {
                var feature = QuotaAdapter.Text(item, "metered_feature", QuotaAdapter.Text(item, "meteredFeature"));
                var name = QuotaAdapter.Text(item, "limit_name", QuotaAdapter.Text(item, "limitName", feature));
                var key = Regex.Replace((feature.Length > 0 ? feature : name).ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
                var spark = key is "spark" or "codex_spark" or "gpt_5_3_codex_spark";
                var prefix = spark ? "spark" : Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
                // An unknown or duplicate identity must not be merged into main or another model bucket.
                if (key.Length == 0 || prefix.Length == 0 || !prefixes.Add(prefix)) throw new JsonException("Ambiguous additional quota scope");
                AddFamily(Field(item, "rate_limit", "rateLimit"), prefix, spark ? "models" : "feature", spark ? "" : key, spark ? ["gpt-5.3-codex-spark"] : null);
            }
        }
        if (windows.Count == 0) throw new JsonException("No readable quota windows");
        return windows.ToArray();
    }
    public static object Target(AccountSelection a) => new { auth_file_snapshot = a.AuthFile, auth_index = a.AuthIndex,
        auth_account_id_snapshot = a.AccountId, auth_provider_snapshot = a.Provider, account_snapshot = a.AccountSnapshot,
        auth_label_snapshot = a.AuthLabel, auth_project_id_snapshot = a.ProjectId, source = a.AuthFile };
    public static object SnapshotWrite(AccountSelection a, Dictionary<string, object?>[] windows, bool completeInventory = false) => new {
        entries = new[] { new { row_key = a.Id, provider = a.Provider, account = Target(a), windows,
            observation = new { source = "api_query", observed_at_ms = windows[0]["observed_at_ms"],
                inventory_scope_key = a.Provider == "codex" ? "codex:rate-limits" : a.Provider + ":quota-windows",
                inventory_mode = completeInventory ? "complete" : "partial" } } }
    };
}
