namespace WallpaperBridge;

public sealed record Metric(double? Value, string Unit = "%", string Source = "unavailable", string? SensorId = null);
public sealed record Device(string Name, Metric Load, Metric Temperature, double? UsedBytes = null, double? TotalBytes = null);
public sealed record HardwareData(string Status, DateTimeOffset? ObservedAt, Device Cpu, Device Gpu, Device Vram, Device Ram, string Message);
public sealed record QuotaWindow(string Id, string Label, double? RemainingPercent, double? UsedValue, double? LimitValue,
    string QuotaUnit, DateTimeOffset? ResetAt, long? DurationSeconds, string Mode, string Accuracy, DateTimeOffset? ObservedAt, bool Stale);
public sealed record ResetCreditData(int? Available, DateTimeOffset? ObservedAt, bool Stale);
public sealed record QuotaAccount(string Id, string Alias, string Provider, string Plan, QuotaWindow[] Windows, ResetCreditData? ResetCredits = null);
public sealed record RequestActivity(DateTimeOffset Time, string Model, bool? Success, int? StatusCode, long? Tokens, long? InputTokens, long? OutputTokens, long? CachedTokens, double? DurationMs, double? TtftMs = null);
public sealed record AccountActivity(string AccountId, string Status, DateTimeOffset? FetchedAt, DateTimeOffset From, DateTimeOffset To,
    long? Requests, double? SuccessRate, long? Tokens, double? EstimatedCost, DateTimeOffset? LastActivity, RequestActivity[] Recent, bool HasMore, string Message, long? NextBeforeMs = null, long? NextBeforeId = null, long? TotalCount = null);
public sealed record QuotaData(string Status, DateTimeOffset? FetchedAt, string Message, QuotaAccount[] Accounts);
public sealed record WeatherHour(DateTimeOffset Time, double? Temperature, double? PrecipitationProbability, double? PrecipitationMm, int? Code);
public sealed record WeatherData(string Status, DateTimeOffset? FetchedAt, DateTimeOffset? ObservedAt, string City,
    double? Temperature, double? Low, double? High, int? Code, bool IsDay, string Message, WeatherHour[]? Hourly = null, string? Timezone = null);
public sealed record Snapshot(int SchemaVersion, DateTimeOffset GeneratedAt, HardwareData Hardware, QuotaData Quota, WeatherData Weather);
public sealed record City(string Id, string Name, double Latitude, double Longitude, string Timezone);
public sealed record AccountSelection(string Id, string Alias, string Provider, string AuthFile, string AuthIndex, string AccountId,
    string AccountSnapshot = "", string AuthLabel = "", string ProjectId = "");
public sealed record SensorSelection(string? GpuId = null, string? CpuTemperatureId = null, string? GpuTemperatureId = null);
public sealed record BridgeConfig(int Port, string CpampUrl, string? ManagementKey, City City, AccountSelection[] Accounts, SensorSelection Sensors,
    bool QuotaRefreshEnabled = true, int QuotaRefreshMinutes = 10)
{
    public static BridgeConfig Default => new(17632, "https://cpa.gdp.pw", null,
        new City("1886760", "苏州 · 江苏", 31.30408, 120.59538, "Asia/Shanghai"), [], new());
}
public sealed record RefreshAccountResult(string Alias, string Status, string Message);
public sealed record QuotaRefreshState(DateTimeOffset? LastAttemptAt = null, DateTimeOffset? LastSuccessAt = null,
    DateTimeOffset? LastFinishedAt = null, string Status = "idle", string Message = "尚未向上游刷新", RefreshAccountResult[]? Accounts = null);
public sealed record SensorInfo(string Id, string HardwareId, string HardwareType, string Hardware, string Name, string Type, double? Value);
public static class Empty
{
    public static Device Device(string name = "待检测") => new(name, new(null), new(null, "°C"));
    public static HardwareData Hardware => new("empty", null, Device(), Device(), Device("专用显存"), Device("物理内存"), "正在检测硬件");
    public static QuotaData Quota => new("unconfigured", null, "在本机设置中连接 CPAMP", []);
    public static WeatherData Weather(City city) => new("empty", null, null, city.Name, null, null, null, null, true, "正在获取天气");
}
