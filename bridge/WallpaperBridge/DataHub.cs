namespace WallpaperBridge;

public sealed class DataHub
{
    private readonly object gate = new();
    private HardwareData hardware = Empty.Hardware;
    private QuotaData quota;
    private WeatherData weather;
    public DataHub(PrivateStore store)
    {
        quota = !string.IsNullOrEmpty(store.Config.ManagementKey) && store.Read<QuotaData>("quota-cache") is { } q ? q with { Status = "stale", Message = "上次缓存，正在连接", Accounts = store.Config.Accounts.Select(s => q.Accounts.FirstOrDefault(a => a.Id == s.Id) is { } a ? a with { Alias = s.Alias, Windows = a.Windows.Select(w => w with { Stale = true }).ToArray(), ResetCredits = a.ResetCredits is { } credits ? credits with { Stale = true } : null } : new QuotaAccount(s.Id, s.Alias, s.Provider, "", [])).ToArray() } : Empty.Quota;
        weather = store.Read<WeatherData>("weather-cache") is { } w && w.City == store.Config.City.Name ? w with { Status = "stale", Message = "上次缓存，正在连接" } : Empty.Weather(store.Config.City);
    }
    public void Hardware(HardwareData data) { lock (gate) hardware = data; }
    public void Quota(QuotaData data) { lock (gate) quota = data; }
    public void Weather(WeatherData data) { lock (gate) weather = data; }
    public Snapshot Snapshot() { lock (gate) return new(1, DateTimeOffset.UtcNow, hardware, quota, weather); }
}
