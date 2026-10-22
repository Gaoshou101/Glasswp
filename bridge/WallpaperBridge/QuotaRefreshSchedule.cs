namespace WallpaperBridge;

public static class QuotaRefreshSchedule
{
    public static DateTimeOffset? Next(BridgeConfig config, QuotaRefreshState state, DateTimeOffset now)
    {
        if (!config.QuotaRefreshEnabled || string.IsNullOrEmpty(config.ManagementKey) || config.Accounts.Length == 0) return null;
        // Persist the attempt before sending; a restart cannot cause an immediate repeat.
        var basis = state.LastFinishedAt is { } finished && (state.LastAttemptAt is null || finished > state.LastAttemptAt) ? finished : state.LastAttemptAt;
        return basis?.AddMinutes(config.QuotaRefreshMinutes) ?? now;
    }
    public static bool Due(BridgeConfig config, QuotaRefreshState state, DateTimeOffset now) => Next(config, state, now) is { } next && now >= next;
}
