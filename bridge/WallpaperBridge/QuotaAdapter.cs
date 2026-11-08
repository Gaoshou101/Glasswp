using System.Text.Json;

namespace WallpaperBridge;

public static class QuotaAdapter
{
    public static string WindowLabel(long? duration, string kind, string scope)
    {
        var label = duration == 18000 ? "5 小时窗口" : duration == 604800 ? "周窗口" : duration == 86400 || kind == "daily" ? "日窗口" : kind == "monthly" ? "月窗口" : kind;
        if (scope.Length > 0 && scope is not ("all" or "codex_main")) label += " · " + (scope switch { "code_review" => "代码审查", "base_model_inference" => "基础模型", "gpt-5.3-codex-spark" => "Codex Spark", "imagegen" => "绘图", "imagegen_premium" => "绘图（Premium）", _ => scope.StartsWith("imagegen_", StringComparison.Ordinal) ? "绘图（" + scope[9..] + "）" : scope });
        return label;
    }
    // Readable plan names for the wallpaper and settings page; CPAMP keeps the raw provider keys.
    // Codex keys follow CPAMP v1.14.10 plans/providers/codex.ts. Unknown or already readable values pass through.
    public static string PlanName(string provider, string plan) => provider switch
    {
        "claude" => ClaudeQuotaAdapter.PlanName(plan),
        "codex" => plan.Trim().ToLowerInvariant() switch
        {
            "" => "",
            "free" => "GPT Free", "go" => "GPT Go", "plus" => "GPT Plus",
            "prolite" or "pro-lite" or "pro_lite" or "pro_5x" => "GPT Pro 5x", "pro" or "pro_20x" => "GPT Pro 20x",
            "team" => "GPT Team", "business" => "GPT Business",
            "self_serve_business_prolite" or "business_premium_5x" => "GPT Business 5x",
            "self_serve_business_usage_based" or "business_usage_based" => "GPT Business PAYG",
            "ent26" or "enterprise" or "hc" => "GPT Enterprise",
            "enterprise_cbp_automation" or "enterprise_automation" => "GPT Ent. Auto",
            "enterprise_cbp_usage_based" or "enterprise_usage_based" => "GPT Ent. PAYG",
            "edu" or "education" => "GPT Edu", "edu_plus" => "GPT Edu Plus", "edu_pro" => "GPT Edu Pro",
            _ => plan.Trim()
        },
        _ => plan
    };
    public static JsonElement Unwrap(JsonElement root) => root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;
    public static string Text(JsonElement node, string name, string fallback = "") => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    public static double? Number(JsonElement node, string name) => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    public static DateTimeOffset? Timestamp(JsonElement node, string name)
    {
        var value = Number(node, name);
        if (value is null or <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds((long)value.Value); } catch (ArgumentOutOfRangeException) { return null; }
    }
    public static QuotaAccount[] Parse(JsonElement response, AccountSelection[] selected, DateTimeOffset now)
    {
        var root = Unwrap(response);
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) throw new JsonException("Missing items");
        var accounts = new List<QuotaAccount>();
        foreach (var item in items.EnumerateArray())
        {
            var target = selected.FirstOrDefault(a => a.Id == Text(item, "row_key"));
            if (target is null) continue;
            if (!item.TryGetProperty("windows", out var windows) || windows.ValueKind != JsonValueKind.Array) throw new JsonException("Missing windows");
            var result = new List<QuotaWindow>();
            var plan = "";
            ResetCreditData? resetCredits = null;
            foreach (var w in windows.EnumerateArray())
            {
                if (Text(w, "availability").Trim().ToLowerInvariant() is "inactive" or "pending_absent" or "removed" or "missing" or "disabled") continue;
                if (target.Provider == "codex" && Number(w, "reset_credits_available") is >= 0 and <= int.MaxValue and var available && available == Math.Truncate(available))
                {
                    var creditAt = Timestamp(w, "observed_at_ms");
                    if (w.TryGetProperty("field_sources", out var creditSources) && creditSources.ValueKind == JsonValueKind.Object && creditSources.TryGetProperty("reset_credits_available", out var creditField) && creditField.ValueKind == JsonValueKind.Object)
                        creditAt = Timestamp(creditField, "observed_at_ms");
                    var creditStale = creditAt is null || creditAt > now.AddMinutes(1) || now - creditAt > TimeSpan.FromMinutes(15) || w.TryGetProperty("stale", out var creditFlag) && creditFlag.ValueKind == JsonValueKind.True;
                    if (resetCredits is null || creditAt > resetCredits.ObservedAt || resetCredits.ObservedAt is null)
                        resetCredits = new((int)available, creditAt, creditStale);
                    else if (creditAt == resetCredits.ObservedAt && resetCredits.Available != (int)available)
                        resetCredits = new(null, creditAt, true);
                }
                var remaining = Number(w, "remaining_percent");
                if (remaining is null && Number(w, "used_percent") is { } used) remaining = 100 - used;
                if (remaining is not null) remaining = Math.Clamp(remaining.Value, 0, 100);
                var observed = Timestamp(w, "observed_at_ms");
                // A newer boundary field does not make an older balance fresh.
                if (w.TryGetProperty("field_sources", out var sources) && sources.ValueKind == JsonValueKind.Object)
                {
                    var key = Number(w, "remaining_percent") is not null ? "remaining_percent" : "used_percent";
                    if ((sources.TryGetProperty("quota", out var field) || sources.TryGetProperty(key, out field)) && field.ValueKind == JsonValueKind.Object) observed = Timestamp(field, "observed_at_ms") ?? observed;
                }
                var stale = w.TryGetProperty("stale", out var flag) && flag.ValueKind == JsonValueKind.True || observed is null || now - observed > TimeSpan.FromMinutes(15);
                var duration = Number(w, "duration_seconds");
                var kind = Text(w, "window_kind", "额度窗口");
                var scope = Text(w, "model_scope_key");
                var label = WindowLabel(duration is > 0 ? (long)duration : null, kind, scope);
                if (target.Provider == "antigravity" && scope is "claude_gpt" or "gemini")
                    label = (scope == "claude_gpt" ? "Claude/GPT" : Text(w, "provider_window_id").StartsWith("gemini-4-argon:", StringComparison.OrdinalIgnoreCase) ? "Gemini 4 Argon" : "Gemini") + " · " + (duration == 18000 ? "5 小时" : duration == 604800 ? "周额度" : duration == 86400 ? "日额度" : Text(w, "provider_window_id").EndsWith("-shared", StringComparison.Ordinal) ? "共享额度" : kind);
                if (target.Provider == "claude")
                {
                    // CPAMP stores no window label. Rebuild it from the stable window ID and model scope.
                    var models = w.TryGetProperty("model_ids", out var ids) && ids.ValueKind == JsonValueKind.Array
                        ? ids.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!.Trim()).ToArray() : [];
                    label = ClaudeQuotaAdapter.Label(Text(w, "provider_window_id"), models) ?? label;
                }
                plan = Text(w, "plan_type", plan);
                result.Add(new(Text(w, "provider_window_id", kind), label, remaining, Number(w, "used_value"), Number(w, "limit_value"), Text(w, "quota_unit"),
                    Timestamp(w, "cycle_end_ms"), duration is > 0 ? (long)duration : null, Text(w, "window_mode", "unknown"), Text(w, "boundary_accuracy", "unknown"), observed, stale));
            }
            accounts.Add(new(target.Id, target.Alias, target.Provider, PlanName(target.Provider, plan), result.ToArray(), resetCredits));
        }
        return accounts.ToArray();
    }
}
