using System.Diagnostics;

namespace WallpaperBridge;

public sealed record ClockSettings(string Timezone, double OffsetSeconds, bool Seconds, bool Hour12, string[] WorldZones)
{
    public static ClockSettings Default => new("Asia/Shanghai", 0, false, false,
        ["Asia/Shanghai", "Asia/Tokyo", "Asia/Singapore", "Europe/London", "America/New_York", "America/Los_Angeles"]);

    public static void Validate(ClockSettings settings)
    {
        if (!ValidZone(settings.Timezone)) throw new ArgumentException("请选择有效的 IANA 显示时区");
        if (!double.IsFinite(settings.OffsetSeconds) || Math.Abs(settings.OffsetSeconds) > 86400)
            throw new ArgumentException("时间校准需在正负 86400 秒以内");
        if (settings.WorldZones is null || settings.WorldZones.Length > 24 || settings.WorldZones.Any(z => !ValidZone(z))
            || settings.WorldZones.Distinct(StringComparer.Ordinal).Count() != settings.WorldZones.Length)
            throw new ArgumentException("世界时钟最多 24 个，需使用有效且不重复的 IANA 时区");
    }

    private static bool ValidZone(string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone) || zone.Length > 100) return false;
        // Windows identifiers are accepted by .NET but cannot be used by browser Intl.
        if (zone != "UTC" && !TimeZoneInfo.TryConvertIanaIdToWindowsId(zone, out _)) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(zone); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}

public sealed record WeatherSettings(string Provider = "open-meteo", string? ApiKey = null, string? ApiHost = null, string? CityCode = null);
public sealed record WeatherSettingsInput(string Provider, string? ApiKey = null, bool ClearApiKey = false, string? ApiHost = null, string? CityCode = null)
{
    public WeatherSettings Apply(WeatherSettings? previous)
    {
        var sameProvider = previous?.Provider == Provider;
        var host = Provider == "qweather" ? (ApiHost ?? (sameProvider ? previous?.ApiHost : null))?.Trim().ToLowerInvariant() : null;
        var code = Provider == "amap" ? (CityCode ?? (sameProvider ? previous?.CityCode : null))?.Trim() : null;
        var sameCredentialScope = sameProvider && (Provider != "qweather" || host == previous?.ApiHost?.Trim().ToLowerInvariant());
        var key = ClearApiKey ? null : string.IsNullOrWhiteSpace(ApiKey)
            ? sameCredentialScope ? previous?.ApiKey : null : ApiKey.Trim();
        if (!ClearApiKey && (Provider is "openweathermap" or "weatherapi" or "amap" or "qweather") && string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("此天气数据源需要 API Key");
        if (Provider == "qweather" && !WeatherProviders.ValidQWeatherHost(host)) throw new ArgumentException("请填写和风控制台中的 API Host，例如 demo.re.qweatherapi.com");
        if (Provider == "amap" && !WeatherProviders.ValidAmapCityCode(code)) throw new ArgumentException("请填写高德天气城市的 6 位行政区代码");
        return new(Provider, Provider is "open-meteo" or "met-no" ? null : key, host, code);
    }
}

public sealed record TimeSyncResult(double OffsetSeconds, DateTimeOffset MeasuredAt, string Source, double RoundTripMs);

/// <summary>Approximate display-only calibration using an authenticated HTTPS Date header.</summary>
public sealed class DisplayTimeSync : IDisposable
{
    private readonly HttpClient client;
    public DisplayTimeSync(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 4096 };
    }

    public async Task<TimeSyncResult> Measure(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, "https://www.cloudflare.com/");
        request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
        var start = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        timer.Stop();
        response.EnsureSuccessStatusCode();
        if (response.Headers.Date is not { } serverTime || response.Headers.Age is { TotalSeconds: > 0 })
            throw new InvalidOperationException("校时响应没有可用时间，请手动校准或重试");
        var offset = Math.Round((serverTime - start.AddMilliseconds(timer.Elapsed.TotalMilliseconds / 2)).TotalSeconds, 3);
        if (!double.IsFinite(offset) || Math.Abs(offset) > 86400)
            throw new InvalidOperationException("系统时间偏差超过一天，请先检查系统时钟");
        return new(offset, DateTimeOffset.UtcNow, "Cloudflare HTTPS Date（约 1 秒精度）", Math.Round(timer.Elapsed.TotalMilliseconds, 1));
    }

    public void Dispose() => client.Dispose();
}
