using System.Globalization;
using System.Text.Json;

namespace WallpaperBridge;

public static class CachedHeaderAdapter
{
    public static string StringOrNumber(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var v)) return "";
        return v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }
    public static AccountSelection Identity(JsonElement file, AccountSelection selected)
    {
        var accountId = QuotaAdapter.Text(file, "account_id");
        if (file.TryGetProperty("id_token", out var idToken) && idToken.ValueKind == JsonValueKind.Object) accountId = QuotaAdapter.Text(idToken, "chatgpt_account_id", accountId);
        var member = QuotaAdapter.Text(file, "account", QuotaAdapter.Text(file, "email"));
        if (selected.Provider == "codex")
        {
            var members = new[] { "accountSnapshot", "account_snapshot", "account", "email" }.Select(key => QuotaAdapter.Text(file, key).Trim(' ').ToLowerInvariant())
                .Where(value => value.Count(c => c == '@') == 1 && value.IndexOf('@') > 0 && !value.EndsWith('@') && value.All(c => c > 0x20 && c < 0x7f)).Distinct().ToArray();
            member = members.Length == 1 ? members[0] : "";
        }
        return selected with { AuthIndex = StringOrNumber(file, "auth_index"), AccountId = accountId, AccountSnapshot = member,
            AuthLabel = QuotaAdapter.Text(file, "label"), ProjectId = selected.Provider == "codex" ? "" : QuotaAdapter.Text(file, "project_id") };
    }
    public static QuotaAccount ParseCodex(JsonElement file, AccountSelection selected, DateTimeOffset now)
    {
        var windows = new List<QuotaWindow>();
        var plan = "";
        if (selected.Provider != "codex" || !file.TryGetProperty("quota", out var quota) || quota.ValueKind != JsonValueKind.Object ||
            !quota.TryGetProperty("signals", out var signals) || signals.ValueKind != JsonValueKind.Object) return new(selected.Id, selected.Alias, selected.Provider, plan, []);
        DateTimeOffset? observed = DateTimeOffset.TryParse(QuotaAdapter.Text(quota, "observed_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        // Header values belong to the historical response observation, never to fetch time.
        double? Value(string key) => double.TryParse(StringOrNumber(signals, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
        plan = QuotaAdapter.Text(signals, "X-Codex-Plan-Type");
        var activeLimit = StringOrNumber(signals, "X-Codex-Active-Limit").Trim().ToLowerInvariant();
        var imageScope = activeLimit == "imagegen" || activeLimit.StartsWith("imagegen_", StringComparison.Ordinal) ? activeLimit : "";
        foreach (var name in new[] { "Primary", "Secondary" })
        {
            var prefix = "X-Codex-" + name + "-";
            var minutes = Value(prefix + "Window-Minutes");
            var used = Value(prefix + "Used-Percent");
            // A zero-minute secondary window is absent, even when its header says 0% used.
            if (minutes is not > 0 || used is null || observed is null) continue;
            DateTimeOffset? reset = null;
            var accuracy = "unknown";
            try
            {
                if (Value(prefix + "Reset-At") is > 0 and < 253402300800 and var epoch) { reset = DateTimeOffset.FromUnixTimeSeconds((long)epoch); accuracy = "exact"; }
                else if (Value(prefix + "Reset-After-Seconds") is > 0 and < 31536000 and var after) { reset = observed.Value.AddSeconds(after); accuracy = "derived"; }
            }
            catch (ArgumentOutOfRangeException) { reset = null; accuracy = "unknown"; }
            var duration = (long)(minutes.Value * 60);
            var label = duration == 18000 ? "5 小时窗口" : duration == 604800 ? "周窗口" : $"{minutes:0} 分钟窗口";
            if (imageScope.Length > 0) label = QuotaAdapter.WindowLabel(duration, label, imageScope);
            var windowId = "cached-header-" + (imageScope.Length > 0 ? imageScope.Replace('_', '-') + "-" : "") + name.ToLowerInvariant();
            windows.Add(new(windowId, label, Math.Clamp(100 - used.Value, 0, 100), null, null, "", reset, duration, "fixed", accuracy, observed,
                now - observed > TimeSpan.FromMinutes(15)));
        }
        return new(selected.Id, selected.Alias, selected.Provider, plan, windows.ToArray());
    }
    public static QuotaAccount Merge(QuotaAccount snapshot, QuotaAccount cachedHeader, bool snapshotAvailable = true)
    {
        var result = snapshot.Windows.ToList();
        foreach (var header in cachedHeader.Windows)
        {
            // A successful snapshot query is authoritative, including absences.
            // Raw headers may only supply windows during a snapshot read failure.
            if (snapshotAvailable) continue;
            var scopedHeader = header.Label.Contains(" · ");
            var i = result.FindIndex(w => w.DurationSeconds == header.DurationSeconds && (scopedHeader ? w.Label == header.Label : !w.Label.Contains(" · ")));
            if (i < 0) { if (scopedHeader || !result.Any(w => w.DurationSeconds == header.DurationSeconds)) result.Add(header); }
            else if (!snapshotAvailable && (header.ObservedAt > result[i].ObservedAt || result[i].ObservedAt is null)) result[i] = header;
        }
        return snapshot with { Plan = string.IsNullOrEmpty(snapshot.Plan) ? cachedHeader.Plan : snapshot.Plan,
            Windows = result.OrderBy(w => w.Label.Contains(" · ") ? 1 : 0).ThenBy(w => w.Stale ? 1 : 0).ThenBy(w => w.DurationSeconds ?? long.MaxValue).ToArray() };
    }
}
