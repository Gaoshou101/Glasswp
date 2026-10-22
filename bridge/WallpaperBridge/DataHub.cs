namespace WallpaperBridge;

public sealed class DataHub
{
    private readonly object gate = new();
    private readonly string epoch = Guid.NewGuid().ToString("N");
    private long hardwareRevision, quotaRevision, weatherRevision;
    private HardwareData hardware = Empty.Hardware;
    private QuotaData quota;
    private WeatherData weather;
    public DataHub(PrivateStore store)
    {
        quota = !string.IsNullOrEmpty(store.Config.ManagementKey) && store.ReadCache<QuotaData>("quota-cache", q => q.Accounts is not null && q.Accounts.All(a => a is not null && a.Windows is not null && a.Windows.All(w => w is not null))) is { } q ? q with { Status = "stale", Message = "上次缓存，正在连接", Accounts = store.Config.Accounts.Select(s => q.Accounts.FirstOrDefault(a => a.Id == s.Id) is { } a ? a with { Alias = s.Alias, Windows = a.Windows.Select(w => w with { Stale = true }).ToArray(), ResetCredits = a.ResetCredits is { } credits ? credits with { Stale = true } : null } : new QuotaAccount(s.Id, s.Alias, s.Provider, "", [])).ToArray() } : Empty.Quota;
        weather = store.ReadCache<WeatherData>("weather-cache", w => w.City is not null && (w.Hourly is null || w.Hourly.All(h => h is not null))) is { } w && w.City == store.Config.City.Name ? w with { Status = "stale", Message = "上次缓存，正在连接" } : Empty.Weather(store.Config.City);
    }
    public void Hardware(HardwareData data) { lock (gate) { hardware = data; hardwareRevision++; } }
    public void Quota(QuotaData data) { lock (gate) { quota = data; quotaRevision++; } }
    public void Weather(WeatherData data) { lock (gate) { weather = data; weatherRevision++; } }
    public Snapshot Snapshot() { lock (gate) return new(1, DateTimeOffset.UtcNow, hardware, quota, weather, new(epoch, hardwareRevision, quotaRevision, weatherRevision)); }
}
