namespace WallpaperBridge;

public sealed class DataHub
{
    private readonly object gate = new();
    private readonly string epoch = Guid.NewGuid().ToString("N");
    private long hardwareRevision, quotaRevision, weatherRevision;
    private HardwareData hardware = Empty.Hardware;
    private QuotaData quota;
    private readonly Dictionary<string, (AiSourceSettings Settings, QuotaData Data)> aiQuota = [];
    private WeatherData weather;
    private readonly PrivateStore store;
    public DataHub(PrivateStore store)
    {
        this.store = store;
        quota = !string.IsNullOrEmpty(store.Config.ManagementKey) && store.ReadCache<QuotaData>("quota-cache", q => q.Accounts is not null && q.Accounts.All(a => a is not null && a.Windows is not null && a.Windows.All(w => w is not null))) is { } q ? q with { Status = "stale", Message = "上次缓存，正在连接", Accounts = store.Config.Accounts.Select(s => q.Accounts.FirstOrDefault(a => a.Id == s.Id) is { } a ? a with { Alias = s.Alias, Windows = a.Windows.Select(w => w with { Stale = true }).ToArray(), ResetCredits = a.ResetCredits is { } credits ? credits with { Stale = true } : null } : new QuotaAccount(s.Id, s.Alias, s.Provider, "", [])).ToArray() } : Empty.Quota;
        weather = store.ReadCache<WeatherData>("weather-cache", w => w.City is not null && (w.Hourly is null || w.Hourly.All(h => h is not null))) is { } w && !WeatherProviders.NeedsKey(store.Config.Weather ?? new()) && w.CacheIdentity == WeatherProviders.CacheIdentity(store.Config) ? w with { Status = "stale", Message = "上次缓存，正在连接" } : WeatherProviders.EmptyFor(store.Config);
    }
    public void Hardware(HardwareData data) { lock (gate) { hardware = data; hardwareRevision++; } }
    public void Quota(QuotaData data) { lock (gate) { quota = data; quotaRevision++; } }
    public QuotaData CpampQuota() { lock (gate) return quota; }
    public void AiQuota(string source, AiSourceSettings settings, QuotaData data) { lock (gate) { aiQuota[source] = (settings, data); quotaRevision++; } }
    public QuotaData AiQuotaSnapshot() { lock (gate) return Merge(AiParts().ToArray()); }
    private IEnumerable<QuotaData> AiParts()
    {
        var settings = store.Config.AiSources ?? new();
        foreach (var source in AiSourceSettings.ReaderSources)
        {
            if (settings.EffectiveSource != source) continue;
            yield return aiQuota.TryGetValue(source, out var value) && value.Settings.SameConnection(settings) ? value.Data : new("empty", null, source == "local" ? "正在检测本机 AI 登录" : "正在读取所选 AI 来源", []);
        }
    }
    internal static QuotaData Merge(params QuotaData[] parts)
    {
        if (parts.Length == 0) return Empty.Quota;
        if (parts.Length == 1) return parts[0];
        var accounts = parts.SelectMany(p => p.Accounts).GroupBy(a => a.Id).Select(g => g.First()).ToArray();
        var dates = parts.Where(p => p.FetchedAt is not null).Select(p => p.FetchedAt!.Value).ToArray();
        var failed = parts.Any(p => p.Status is not ("ok" or "empty" or "unconfigured"));
        return new(accounts.Length == 0 ? failed ? "offline" : "empty" : failed ? "stale" : "ok", dates.Length == 0 ? null : dates.Min(), string.Join("；", parts.Select(p => p.Message).Where(m => m.Length > 0).Distinct()), accounts);
    }
    private QuotaData AllQuota()
    {
        var settings = store.Config.AiSources ?? new();
        return AiSourceSettings.UsesCpamp(store.Config) ? quota : settings.Options(settings.EffectiveSource).Display(AiQuotaSnapshot());
    }
    public void Weather(WeatherData data) { lock (gate) { weather = data; weatherRevision++; } }
    public Snapshot Snapshot() { lock (gate) return new(1, DateTimeOffset.UtcNow, hardware, AllQuota(), weather, new(epoch, hardwareRevision, quotaRevision + store.Revision, weatherRevision), store.Config.Clock, (store.Config.Appearance ?? WallpaperAppearance.Default).Normalize()); }
}
