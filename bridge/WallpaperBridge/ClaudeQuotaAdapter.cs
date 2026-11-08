using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WallpaperBridge;

// Match CPAMP v1.14.10 Claude quota reads. CPA supplies the OAuth token by authIndex.
public static class ClaudeQuotaAdapter
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";
    private const long FiveHours = 18000, SevenDays = 604800;
    // Top-level usage keys in CPAMP order; scoped weekly windows follow seven_day.
    private static readonly (string Key, string Id, long Duration, string Scope)[] TopLevel = [
        ("five_hour", "five-hour", FiveHours, ""), ("seven_day", "seven-day", SevenDays, ""),
        ("seven_day_oauth_apps", "seven-day-oauth-apps", SevenDays, "OAuth 应用"), ("seven_day_opus", "seven-day-opus", SevenDays, "Opus"),
        ("seven_day_sonnet", "seven-day-sonnet", SevenDays, "Sonnet"), ("seven_day_cowork", "seven-day-cowork", SevenDays, "Cowork"),
        ("iguana_necktie", "iguana-necktie", SevenDays, "Iguana Necktie")];

    public static object Request(AccountSelection account, string url) => new {
        authIndex = account.AuthIndex, method = "GET", url,
        header = new Dictionary<string, string> { ["Authorization"] = "Bearer $TOKEN$", ["Content-Type"] = "application/json",
            ["anthropic-beta"] = "oauth-2025-04-20" }
    };

    private static JsonElement Field(JsonElement node, params string[] names)
    {
        if (node.ValueKind != JsonValueKind.Object) return default;
        // Match JavaScript `a ?? b`: the first present non-null member wins.
        foreach (var name in names)
            if (node.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return value;
        return default;
    }
    private static double? Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n
        : value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && double.IsFinite(n) ? n : null;
    private static bool? Flag(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number => Number(value) is { } n ? n != 0 : null,
        JsonValueKind.String => value.GetString()!.Trim().ToLowerInvariant() switch {
            "true" or "1" or "yes" or "y" or "on" => true, "false" or "0" or "no" or "n" or "off" => false, _ => null },
        _ => null
    };
    private static string Token(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
        return Regex.Replace(Regex.Replace(text, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
    }
    // Numeric resets below 1e12 are Unix seconds; larger values are milliseconds.
    public static DateTimeOffset? Reset(JsonElement value)
    {
        try
        {
            if (Number(value) is { } numeric)
                return numeric <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds((long)(numeric < 1e12 ? numeric * 1000 : numeric));
            if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString()!.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return at;
        }
        catch (ArgumentOutOfRangeException) { }
        return null;
    }
    private static string Normalize(string text) { try { return text.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { return text; } }
    // JavaScript encodeURIComponent keeps !'()* unescaped; .NET escapes them.
    private static string EncodeComponent(string text) => Uri.EscapeDataString(text).Replace("%21", "!").Replace("%27", "'").Replace("%28", "(").Replace("%29", ")").Replace("%2A", "*");

    private sealed record Values(double? Used, DateTimeOffset? Reset);
    private static Values? LimitValues(JsonElement limit)
    {
        var used = Number(Field(limit, "percent", "utilization"));
        var reset = Reset(Field(limit, "resets_at", "resetsAt", "reset_at", "resetAt"));
        if (used is < 0) used = null;
        return used is null && reset is null ? null : new(used, reset);
    }
    private static long Rank(DateTimeOffset? reset) => reset?.ToUnixTimeMilliseconds() ?? -1;
    private static string? BaseLimitId(JsonElement limit)
    {
        var kind = Token(Field(limit, "kind")); var group = Token(Field(limit, "group"));
        if (kind == "session" && group is "" or "session") return "five-hour";
        if (kind is "weekly" or "weekly_all" && group is "" or "weekly" or "weekly_all") return "seven-day";
        return null;
    }
    private static bool WeeklyScoped(JsonElement limit)
    {
        var kind = Token(Field(limit, "kind")); var group = Token(Field(limit, "group"));
        if (group.Length > 0 && group != "weekly") return false;
        return kind is "weekly_scoped" or "weekly_model_scoped" || kind == "model_scoped" && group == "weekly";
    }
    private static Dictionary<string, Values> BaseLimitFallbacks(JsonElement limits)
    {
        var best = new Dictionary<string, (int Completeness, int Kind, long Reset, double Used, Values Values)>();
        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object || Flag(Field(limit, "is_active", "isActive")) == false || Field(limit, "scope").ValueKind != JsonValueKind.Undefined) continue;
            if (BaseLimitId(limit) is not { } id || LimitValues(limit) is not { } values) continue;
            var candidate = ((values.Used is not null ? 1 : 0) + (values.Reset is not null ? 1 : 0), id == "seven-day" && Token(Field(limit, "kind")) == "weekly_all" ? 1 : 0, Rank(values.Reset), values.Used ?? -1, values);
            if (best.TryGetValue(id, out var existing))
            {
                var replace = candidate.Item3 != existing.Reset ? candidate.Item3 > existing.Reset
                    : candidate.Item1 != existing.Completeness ? candidate.Item1 > existing.Completeness
                    : candidate.Item2 != existing.Kind ? candidate.Item2 > existing.Kind : candidate.Item4 > existing.Used;
                if (!replace) continue;
            }
            best[id] = candidate;
        }
        return best.ToDictionary(pair => pair.Key, pair => pair.Value.Values);
    }

    private sealed record Scoped(int Activity, string Identity, string? ModelId, string SortLabel, string IdPart, Values Values)
    {
        public long ResetRank => Rank(Values.Reset);
        public double UsedRank => Values.Used ?? -1;
        public int Completeness => (Values.Used is not null ? 1 : 0) + (Values.Reset is not null ? 1 : 0);
    }
    private static bool Replaces(Scoped existing, Scoped candidate)
    {
        if (existing.ResetRank >= 0 && candidate.ResetRank >= 0 && candidate.ResetRank != existing.ResetRank) return candidate.ResetRank > existing.ResetRank;
        if (candidate.Activity != existing.Activity) return candidate.Activity > existing.Activity;
        if (candidate.Completeness != existing.Completeness) return candidate.Completeness > existing.Completeness;
        if (existing.ResetRank >= 0 != candidate.ResetRank >= 0) return candidate.ResetRank >= 0;
        if (candidate.ResetRank != existing.ResetRank) return candidate.ResetRank > existing.ResetRank;
        return candidate.UsedRank > existing.UsedRank;
    }
    private static string? DisplayName(JsonElement model)
    {
        static string? Read(JsonElement node) => Field(node, "display_name", "displayName", "name") is { ValueKind: JsonValueKind.String } value
            && Regex.Replace(value.GetString()!.Trim(), @"\s+", " ") is { Length: > 0 } name ? name : null;
        return Read(model) ?? (Field(model, "details") is { ValueKind: JsonValueKind.Object } details ? Read(details) : null);
    }
    private static Scoped[] ScopedWeekly(JsonElement limits)
    {
        var byId = new Dictionary<string, Scoped>(StringComparer.Ordinal);
        var byLabel = new Dictionary<string, Scoped>(StringComparer.Ordinal);
        var idKeysByLabel = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object || !WeeklyScoped(limit)) continue;
            var model = Field(Field(limit, "scope"), "model");
            if (model.ValueKind != JsonValueKind.Object || DisplayName(model) is not { } label || LimitValues(limit) is not { } values) continue;
            var rawId = Field(model, "id", "model_id", "modelId");
            var modelId = rawId.ValueKind == JsonValueKind.String && rawId.GetString()!.Trim() is { Length: > 0 } id ? Normalize(id) : null;
            var labelKey = Normalize(label).ToLowerInvariant();
            var identity = modelId is not null ? "id:" + modelId : "label:" + labelKey;
            var active = Flag(Field(limit, "is_active", "isActive"));
            var candidate = new Scoped(active == true ? 2 : active is null ? 1 : 0, identity, modelId, labelKey,
                modelId is not null ? "id-" + EncodeComponent(modelId) : EncodeComponent(labelKey), values);
            var target = modelId is not null ? byId : byLabel;
            if (modelId is not null)
            {
                if (!idKeysByLabel.TryGetValue(labelKey, out var keys)) idKeysByLabel[labelKey] = keys = new(StringComparer.Ordinal);
                keys.Add(identity);
            }
            if (target.TryGetValue(identity, out var existing) && !Replaces(existing, candidate)) continue;
            target[identity] = candidate;
        }
        // A label-only duplicate of a single identified model carries no extra evidence.
        foreach (var entry in byLabel.Values.ToArray())
            if (idKeysByLabel.TryGetValue(entry.SortLabel, out var keys) && keys.Count == 1 && byId.TryGetValue(keys.First(), out var identified)
                && identified.Activity == entry.Activity && identified.ResetRank == entry.ResetRank && identified.UsedRank == entry.UsedRank)
                byLabel.Remove(entry.Identity);
        return byId.Values.Concat(byLabel.Values).OrderBy(e => e.SortLabel, StringComparer.Ordinal).ThenBy(e => e.Identity, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, object?> Window(string id, long duration, Values values, DateTimeOffset observed, string? plan, string[]? modelIds = null, bool scopeComplete = true)
    {
        var reliable = scopeComplete && values.Reset is not null;
        var item = new Dictionary<string, object?> {
            ["provider_window_id"] = id, ["window_kind"] = duration == FiveHours ? "five_hour" : "weekly",
            ["window_mode"] = reliable ? "fixed" : "unknown",
            // CPAMP persists an incomplete model scope as feature/scope_unknown.
            ["model_scope_kind"] = !scopeComplete ? "feature" : modelIds is not null ? "models" : "all",
            ["source"] = "api_query", ["observed_at_ms"] = observed.ToUnixTimeMilliseconds(),
            ["boundary_accuracy"] = reliable ? "exact" : "unknown", ["duration_seconds"] = duration
        };
        if (!scopeComplete) item["model_scope_key"] = "scope_unknown";
        if (modelIds is not null) item["model_ids"] = modelIds;
        if (values.Used is { } used) { item["used_percent"] = Math.Clamp(used, 0, 100); item["remaining_percent"] = Math.Clamp(100 - used, 0, 100); }
        if (values.Reset is { } reset) item["cycle_end_ms"] = reset.ToUnixTimeMilliseconds();
        if (!string.IsNullOrEmpty(plan)) item["plan_type"] = plan;
        return item;
    }

    public static Dictionary<string, object?>[] Windows(JsonElement payload, DateTimeOffset observed, string? plan = null)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new JsonException("Missing Claude usage inventory");
        var limits = Field(payload, "limits");
        var fallbacks = limits.ValueKind == JsonValueKind.Array ? BaseLimitFallbacks(limits) : [];
        var scoped = limits.ValueKind == JsonValueKind.Array ? ScopedWeekly(limits) : [];
        var windows = new List<Dictionary<string, object?>>();
        foreach (var (key, id, duration, _) in TopLevel)
        {
            var rendered = false;
            if (payload.TryGetProperty(key, out var top) && top.ValueKind == JsonValueKind.Object && top.TryGetProperty("utilization", out var utilization))
            {
                var values = new Values(Number(utilization), Reset(Field(top, "resets_at")));
                if (values.Used is not null || values.Reset is not null) { windows.Add(Window(id, duration, values, observed, plan)); rendered = true; }
            }
            if (!rendered && fallbacks.TryGetValue(id, out var fallback)) windows.Add(Window(id, duration, fallback, observed, plan));
            if (key == "seven_day")
                windows.AddRange(scoped.Select(entry => Window("weekly-scoped-" + entry.IdPart, SevenDays, entry.Values, observed, plan,
                    entry.ModelId is not null ? [entry.ModelId] : null, entry.ModelId is not null)));
        }
        var extra = Field(payload, "extra_usage");
        if (Flag(Field(extra, "is_enabled")) == true)
        {
            // Credits are cents; utilization is already a percentage.
            var usedCredits = Number(Field(extra, "used_credits"));
            var monthlyLimit = Number(Field(extra, "monthly_limit"));
            var usedPercent = Number(Field(extra, "utilization")) ?? (usedCredits is >= 0 && monthlyLimit is > 0 ? usedCredits / monthlyLimit * 100 : null);
            if (usedPercent is not null || usedCredits is >= 0 && monthlyLimit is >= 0)
            {
                var item = new Dictionary<string, object?> {
                    ["provider_window_id"] = "extra-usage", ["window_kind"] = "billing", ["window_mode"] = "non_window",
                    ["model_scope_kind"] = "all", ["source"] = "api_query", ["observed_at_ms"] = observed.ToUnixTimeMilliseconds(),
                    ["boundary_accuracy"] = "unknown"
                };
                if (usedPercent is { } used) { item["used_percent"] = Math.Clamp(used, 0, 100); item["remaining_percent"] = Math.Clamp(100 - used, 0, 100); }
                if (usedCredits is >= 0 && monthlyLimit is >= 0) { item["used_value"] = Math.Round(usedCredits.Value / 100, 2); item["limit_value"] = Math.Round(monthlyLimit.Value / 100, 2); item["quota_unit"] = "USD"; }
                if (!string.IsNullOrEmpty(plan)) item["plan_type"] = plan;
                windows.Add(item);
            }
        }
        if (windows.Count == 0) throw new JsonException("No readable Claude quota windows");
        return windows.ToArray();
    }

    // Plan metadata is best effort; an unreadable profile never invents a free plan.
    public static string? ParsePlan(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object) return null;
        var account = Field(profile, "account");
        var max = Flag(Field(account, "has_claude_max"));
        if (max == true) return "plan_max";
        var pro = Flag(Field(account, "has_claude_pro"));
        if (pro == true) return "plan_pro";
        var organization = Field(profile, "organization");
        if (Field(organization, "organization_type") is { ValueKind: JsonValueKind.String } type && type.GetString()!.Trim().Equals("claude_team", StringComparison.OrdinalIgnoreCase)
            && Field(organization, "subscription_status") is { ValueKind: JsonValueKind.String } status && status.GetString()!.Trim().Equals("active", StringComparison.OrdinalIgnoreCase))
            return "plan_team";
        return max == false && pro == false ? "plan_free" : null;
    }
    public static string PlanName(string plan) => plan.Trim().ToLowerInvariant() switch
    {
        "" => "",
        "plan_free" or "free" => "Claude Free", "plan_pro" or "pro" => "Claude Pro", "plan_max" or "max" => "Claude Max",
        "plan_max5" or "max_5x" => "Claude Max 5x", "plan_max20" or "max_20x" => "Claude Max 20x", "plan_team" or "team" => "Claude Team",
        _ => plan.Trim()
    };
    public static string? Label(string windowId, string[] modelIds)
    {
        if (windowId == "extra-usage") return "额外用量";
        if (windowId == "five-hour") return "5 小时窗口";
        foreach (var (_, id, _, scope) in TopLevel)
            if (id == windowId) return scope.Length == 0 ? "周窗口" : "周窗口 · " + scope;
        if (!windowId.StartsWith("weekly-scoped-", StringComparison.Ordinal)) return null;
        var part = windowId["weekly-scoped-".Length..];
        string model;
        try
        {
            // Model IDs are shown as-is. A name-only window ID holds the lowercased display name,
            // so restore an initial capital on each word ("fable" -> "Fable").
            model = modelIds.FirstOrDefault(m => m.Length > 0) ?? (part.StartsWith("id-", StringComparison.Ordinal) ? Uri.UnescapeDataString(part[3..])
                : string.Join(' ', Uri.UnescapeDataString(part).Split(' ').Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..])));
        }
        catch (UriFormatException) { model = part; }
        return model.Length > 0 ? "周窗口 · " + model : "周窗口";
    }
}