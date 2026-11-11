using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WallpaperBridge;

/// <summary>Fixed upstream endpoints; credentials never enter display snapshots.</summary>
public sealed class WeatherProviders
{
    private JsonElement? metForecast;
    private string? metIdentity;
    private DateTimeOffset metExpires;
    public static readonly string[] Supported = ["open-meteo", "met-no", "openweathermap", "weatherapi", "amap", "qweather"];
    public static void ValidateSettings(WeatherSettings settings)
    {
        if (!Supported.Contains(settings.Provider)) throw new ArgumentException("天气数据源无效");
        if (settings.ApiKey is { } key && (key.Length > 512 || key.Any(char.IsControl))) throw new ArgumentException("天气凭据格式无效");
        if (settings.Provider == "amap" && !ValidAmapCityCode(settings.CityCode)) throw new ArgumentException("高德天气需要 6 位城市 adcode");
        if (settings.Provider == "qweather" && !ValidQWeatherHost(settings.ApiHost)) throw new ArgumentException("和风 API Host 需为专属的 xxx.qweatherapi.com 主机名");
    }
    public static bool ValidAmapCityCode(string? value) => value is { Length: 6 } && value.All(char.IsAsciiDigit);
    public static bool ValidQWeatherHost(string? value) => value is { Length: <= 253 } && Regex.IsMatch(value,
        "\\A(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+qweatherapi\\.com\\z", RegexOptions.CultureInvariant);
    public static string CacheIdentity(BridgeConfig cfg) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('|', (cfg.Weather ?? new()).Provider, cfg.City.Id, cfg.City.Name,
            cfg.City.Latitude.ToString("R", CultureInfo.InvariantCulture), cfg.City.Longitude.ToString("R", CultureInfo.InvariantCulture), cfg.City.Timezone,
            (cfg.Weather ?? new()).ApiHost, (cfg.Weather ?? new()).CityCode))));
    public static bool SameSource(BridgeConfig a, BridgeConfig b) => a.City == b.City && (a.Weather ?? new()) == (b.Weather ?? new());
    public static WeatherData EmptyFor(BridgeConfig cfg) => Empty.Weather(cfg.City) with { Provider = (cfg.Weather ?? new()).Provider, CacheIdentity = CacheIdentity(cfg) };

    public async Task<WeatherData> FetchAsync(HttpClient client, BridgeConfig cfg, CancellationToken token)
    {
        var settings = cfg.Weather ?? new();
        ValidateSettings(settings);
        if (NeedsKey(settings)) throw new ArgumentException("此天气数据源需要 API Key");
        var city = cfg.City;
        var coords = FormattableString.Invariant($"lat={city.Latitude:F4}&lon={city.Longitude:F4}");
        var now = DateTimeOffset.UtcNow;
        WeatherData data;
        switch (settings.Provider)
        {
            case "amap":
                var amapQuery = "https://restapi.amap.com/v3/weather/weatherInfo?city=" + settings.CityCode + "&key=" + Uri.EscapeDataString(settings.ApiKey!.Trim()) + "&output=JSON&extensions=";
                var amapCurrent = await Read(client, amapQuery + "base", token);
                CheckAmap(amapCurrent);
                JsonElement? amapDaily = null;
                try { amapDaily = await Read(client, amapQuery + "all", token); CheckAmap(amapDaily.Value); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) when (OptionalForecastFailure(e)) { amapDaily = null; }
                data = ParseAmap(amapCurrent, amapDaily, settings.CityCode!, now);
                break;
            case "qweather":
                var qCoords = FormattableString.Invariant($"{city.Latitude:F2}/{city.Longitude:F2}");
                var qCurrent = await ReadQWeather(client, settings, "/weather/v1/current/" + qCoords + "?lang=zh", token);
                JsonElement? qDaily = null, qHourly = null;
                try { qDaily = await ReadQWeather(client, settings, "/weather/v1/daily/" + qCoords + "?lang=zh&days=3", token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) when (OptionalForecastFailure(e)) { }
                try { qHourly = await ReadQWeather(client, settings, "/weather/v1/hourly/" + qCoords + "?lang=zh&hours=24", token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) when (OptionalForecastFailure(e)) { }
                data = ParseQWeather(qCurrent, qDaily, qHourly, city, now);
                break;
            case "met-no":
                var identity = CacheIdentity(cfg);
                if (metForecast is null || metIdentity != identity || now >= metExpires)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.met.no/weatherapi/locationforecast/2.0/compact?" + coords);
                    request.Headers.UserAgent.ParseAdd("PortraitWallpaperDashboard/0.1");
                    using var response = await client.SendAsync(request, token);
                    response.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    // Honor MET's advertised expiry; re-render its cached forecast locally between fetches.
                    metExpires = response.Content.Headers.Expires is { } expiry && expiry > now ? expiry : now.AddMinutes(15);
                    metForecast = document.RootElement.Clone(); metIdentity = identity;
                }
                data = ParseMet(metForecast.Value, city, now);
                break;
            case "openweathermap":
                var query = coords + "&units=metric&appid=" + Uri.EscapeDataString(settings.ApiKey!.Trim());
                var current = await Read(client, "https://api.openweathermap.org/data/2.5/weather?" + query, token);
                JsonElement? forecast = null;
                try { forecast = await Read(client, "https://api.openweathermap.org/data/2.5/forecast?" + query, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException) { }
                data = ParseOpenWeather(current, forecast, city, now);
                break;
            case "weatherapi":
                var location = FormattableString.Invariant($"{city.Latitude:F4},{city.Longitude:F4}");
                var weatherApi = await Read(client, "https://api.weatherapi.com/v1/forecast.json?key=" + Uri.EscapeDataString(settings.ApiKey!.Trim()) + "&q=" + location + "&days=2&aqi=no&alerts=no", token);
                data = ParseWeatherApi(weatherApi, city, now);
                break;
            default:
                var url = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={city.Latitude}&longitude={city.Longitude}&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&hourly=temperature_2m,precipitation_probability,precipitation,weather_code&timezone={Uri.EscapeDataString(city.Timezone)}&timeformat=unixtime&forecast_days=2");
                data = WeatherAdapter.Parse(await Read(client, url, token), city, now);
                break;
        }
        return data with { Provider = settings.Provider, CacheIdentity = CacheIdentity(cfg) };
    }
    public static bool NeedsKey(WeatherSettings settings) => settings.Provider is "openweathermap" or "weatherapi" or "amap" or "qweather" && string.IsNullOrWhiteSpace(settings.ApiKey);
    private static bool OptionalForecastFailure(Exception e) => e is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException or ArgumentException;
    private static async Task<JsonElement> ReadQWeather(HttpClient client, WeatherSettings settings, string path, CancellationToken token)
    {
        // Host validation occurs before constructing the URI or attaching this host's key.
        if (!ValidQWeatherHost(settings.ApiHost)) throw new ArgumentException("和风 API Host 无效");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + settings.ApiHost + path);
        request.Headers.Add("X-QW-Api-Key", settings.ApiKey!.Trim());
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var source = await response.Content.ReadAsStreamAsync(token);
        using var gzip = response.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase) ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true) : null;
        using var output = new MemoryStream();
        var input = (Stream?)gzip ?? source;
        var buffer = new byte[8192]; int read;
        try
        {
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                if (output.Length + read > 4 * 1024 * 1024) throw new JsonException("Weather response exceeds limit");
                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException e) { throw new JsonException("Invalid compressed weather response", e); }
        using var document = JsonDocument.Parse(output.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) || root.TryGetProperty("code", out var code) && code.ToString() != "200")
            throw new JsonException("QWeather request failed");
        if (path.StartsWith("/weather/v1/daily/", StringComparison.Ordinal) && Child(root, "days").ValueKind != JsonValueKind.Array
            || path.StartsWith("/weather/v1/hourly/", StringComparison.Ordinal) && Child(root, "hours").ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing QWeather forecast");
        return root.Clone();
    }
    private static async Task<JsonElement> Read(HttpClient client, string url, CancellationToken token)
    {
        using var response = await client.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.Clone();
    }
    private static double? Number(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object ? QuotaAdapter.Number(item, name) : null;
    private static double? Numeric(JsonElement item, string name)
    {
        var value = Child(item, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number
            : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number) ? number : null;
    }
    private static JsonElement Child(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;
    private static string Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object ? QuotaAdapter.Text(item, name) : "";
    private static IEnumerable<JsonElement> Items(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static DateTimeOffset? Unix(double? value) => value is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)value.Value) : null;
    private static DateTimeOffset? Stamp(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp) ? stamp : null;
    private static double? Chance(double? value) => value is { } n ? Math.Clamp(n, 0, 100) : null;
    private static double? Amount(double? value) => value is >= 0 ? value : null;
    private static WeatherHour[] Hours(IEnumerable<WeatherHour> hours, DateTimeOffset now) => hours.Where(h => h.Time > now.AddHours(-1) && h.Time <= now.AddHours(24)).OrderBy(h => h.Time).DistinctBy(h => h.Time).Take(25).ToArray();
    private static (double? low, double? high) Range(IEnumerable<WeatherHour> hours, City city, DateTimeOffset now)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(city.Timezone);
        var date = TimeZoneInfo.ConvertTime(now, tz).Date;
        var values = hours.Where(h => TimeZoneInfo.ConvertTime(h.Time, tz).Date == date && h.Temperature.HasValue).Select(h => h.Temperature!.Value).ToArray();
        return values.Length == 0 ? (null, null) : (values.Min(), values.Max());
    }
    private static void CheckAmap(JsonElement root)
    {
        if (Child(root, "status").ToString() != "1" || Child(root, "infocode").ToString() != "10000") throw new JsonException("Amap weather request failed");
    }
    public static WeatherData ParseAmap(JsonElement current, JsonElement? forecast, string cityCode, DateTimeOffset now)
    {
        CheckAmap(current);
        var live = Items(Child(current, "lives")).FirstOrDefault(p => Text(p, "adcode") == cityCode);
        var temperature = Numeric(live, "temperature_float") ?? Numeric(live, "temperature");
        if (temperature is null || string.IsNullOrWhiteSpace(Text(live, "city"))) throw new JsonException("Missing matching Amap current weather");
        var chinaNow = now.ToOffset(TimeSpan.FromHours(8));
        DateTimeOffset? observed = DateTime.TryParseExact(Text(live, "reporttime"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var report)
            ? new DateTimeOffset(DateTime.SpecifyKind(report, DateTimeKind.Unspecified), TimeSpan.FromHours(8)) : null;
        var cast = default(JsonElement);
        if (forecast is { } daily)
        {
            try
            {
                CheckAmap(daily);
                var matching = Items(Child(daily, "forecasts")).FirstOrDefault(p => Text(p, "adcode") == cityCode);
                cast = Items(Child(matching, "casts")).FirstOrDefault(p => Text(p, "date") == chinaNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            catch (JsonException) { }
        }
        var day = Numeric(cast, "daytemp_float") ?? Numeric(cast, "daytemp");
        var night = Numeric(cast, "nighttemp_float") ?? Numeric(cast, "nighttemp");
        double? low = day.HasValue && night.HasValue ? Math.Min(day.Value, night.Value) : null;
        double? high = day.HasValue && night.HasValue ? Math.Max(day.Value, night.Value) : null;
        var province = Text(live, "province");
        var name = Text(live, "city") + (province.Length == 0 ? "" : " · " + province) + " (" + cityCode + ")";
        return new("ok", now, observed, name, temperature, low, high, AmapCode(Text(live, "weather")), chinaNow.Hour is >= 6 and < 18,
            cast.ValueKind == JsonValueKind.Object ? "高德天气 · 实况与日间/夜间温度范围；不提供小时预报" : "高德天气 · 实况；今日预报暂不可用，不提供小时预报", [], "Asia/Shanghai");
    }
    public static int? AmapCode(string value) => value switch {
        "晴" => 0, "少云" => 1, "晴间多云" or "多云" => 2, "阴" => 3,
        "雷阵雨" or "强雷阵雨" => 95, "雷阵雨并伴有冰雹" => 96,
        "小雨" or "小到中雨" => 61, "中雨" or "中到大雨" => 63,
        "大雨" or "暴雨" or "大暴雨" or "特大暴雨" or "大到暴雨" or "暴雨到大暴雨" or "大暴雨到特大暴雨" => 65,
        "阵雨" => 80, "强阵雨" => 82, "毛毛雨/细雨" => 51, "雨" => 63, "冻雨" => 66,
        "小雪" or "小到中雪" => 71, "中雪" or "中到大雪" or "雪" => 73, "大雪" or "暴雪" or "大到暴雪" => 75,
        "雨夹雪" or "雨雪天气" or "阵雨夹雪" => 66, "阵雪" => 85,
        "薄雾" or "雾" or "浓雾" or "强浓雾" or "大雾" or "特强浓雾" => 45,
        // Haze and blowing dust are distinct from WMO fog; leave unsupported phenomena unknown.
        _ => null };
    public static WeatherData ParseQWeather(JsonElement current, JsonElement? daily, JsonElement? hourly, City city, DateTimeOffset now)
    {
        if (current.ValueKind != JsonValueKind.Object || current.TryGetProperty("error", out _) || current.TryGetProperty("code", out var responseCode) && responseCode.ToString() != "200")
            throw new JsonException("QWeather request failed");
        var temperature = Numeric(Child(current, "temperature"), "value");
        if (temperature is null) throw new JsonException("Missing QWeather current temperature");
        var condition = Child(current, "condition");
        var point = daily is { } d ? Items(Child(d, "days")).FirstOrDefault(p =>
            Stamp(Text(p, "forecastStartTime")) is { } start && Stamp(Text(p, "forecastEndTime")) is { } end && start <= now && end > now) : default;
        var sunrise = Stamp(Text(Child(point, "astro"), "sunrise"));
        var sunset = Stamp(Text(Child(point, "astro"), "sunset"));
        var hours = Hours(Items(hourly is { } h ? Child(h, "hours") : default).Select(p => new WeatherHour(
            Stamp(Text(p, "forecastTime")) ?? DateTimeOffset.MinValue, Numeric(Child(p, "temperature"), "value"),
            Chance(Numeric(Child(p, "precipitation"), "probability") * 100), Amount(Numeric(Child(Child(p, "precipitation"), "amount"), "value")), QWeatherCode(Numeric(Child(p, "condition"), "code")))), now);
        var localHour = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(city.Timezone)).Hour;
        var isDay = sunrise.HasValue && sunset.HasValue ? now >= sunrise.Value && now < sunset.Value : localHour is >= 6 and < 18;
        var attributions = new[] { current, daily ?? default, hourly ?? default }.SelectMany(p => Items(Child(Child(p, "metadata"), "attributions")))
            .Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)
            .Where(v => v.Length is > 0 and <= 512 && !v.Any(char.IsControl)).Distinct().Take(8).ToArray();
        var message = "和风天气 · " + (daily.HasValue && hourly.HasValue ? "实时与天气预报" : daily.HasValue ? "实时与每日预报；小时预报暂不可用" : hourly.HasValue ? "实时与小时预报；每日预报暂不可用" : "实况；预报暂不可用");
        if (attributions.Length > 0) message += " · " + string.Join("；", attributions);
        // Weather v1 does not publish an observation timestamp; do not fabricate one from fetch time.
        return new("ok", now, null, city.Name, temperature, Numeric(Child(point, "temperatureMin"), "value"), Numeric(Child(point, "temperatureMax"), "value"), QWeatherCode(Numeric(condition, "code")), isDay,
            message, hours, city.Timezone);
    }
    public static int? QWeatherCode(double? code) => code switch {
        100 or 150 => 0, 102 or 152 => 1, 101 or 103 or 151 or 153 => 2, 104 => 3,
        300 or 350 => 80, 301 or 351 => 82, 302 or 303 => 95, 304 => 96, 305 or 314 => 61, 306 or 315 or 399 => 63,
        307 or 308 or 310 or 311 or 312 or 316 or 317 or 318 => 65, 309 => 51, 313 => 66,
        400 or 408 => 71, 401 or 409 or 499 => 73, 402 or 403 or 410 => 75, 404 or 405 or 406 or 456 => 66, 407 or 457 => 85,
        500 or 501 or 509 or 510 or 514 or 515 => 45, _ => null };
    public static WeatherData ParseMet(JsonElement root, City city, DateTimeOffset now)
    {
        var points = Items(root.GetProperty("properties").GetProperty("timeseries")).Select(p => (time: Stamp(Text(p, "time")), data: Child(p, "data"))).Where(p => p.time.HasValue).OrderBy(p => p.time).ToArray();
        if (points.Length == 0) throw new JsonException("Missing forecast");
        var current = points.MinBy(p => Math.Abs((p.time!.Value - now).TotalSeconds));
        if (current.time < now.AddHours(-1) || current.time > now.AddHours(1)) throw new JsonException("Forecast does not cover the current hour");
        var hours = points.Select(p => {
            var next = Child(p.data, "next_1_hours");
            if (next.ValueKind != JsonValueKind.Object) next = Child(p.data, "next_6_hours");
            return new WeatherHour(p.time!.Value, Number(Child(Child(p.data, "instant"), "details"), "air_temperature"),
                Chance(Number(Child(next, "details"), "probability_of_precipitation")), Amount(Number(Child(next, "details"), "precipitation_amount")), MetCode(Text(Child(next, "summary"), "symbol_code")));
        }).ToArray();
        var currentHour = hours.First(h => h.Time == current.time);
        if (currentHour.Temperature is null) throw new JsonException("Missing current forecast temperature");
        var currentNext = Child(current.data, "next_1_hours");
        if (currentNext.ValueKind != JsonValueKind.Object) currentNext = Child(current.data, "next_6_hours");
        var symbol = Text(Child(currentNext, "summary"), "symbol_code");
        var (low, high) = Range(hours, city, now);
        return new("ok", now, current.time, city.Name, currentHour.Temperature, low, high, currentHour.Code, !symbol.EndsWith("_night", StringComparison.Ordinal),
            "MET Norway · 预报模型；日范围为预报采样范围", Hours(hours, now), city.Timezone);
    }
    public static int? MetCode(string symbol)
    {
        var value = symbol.Split('_')[0];
        if (value.Contains("thunder")) return value.Contains("sleet") || value.Contains("snow") ? 96 : 95;
        if (value.Contains("snow")) return value.StartsWith("heavy") ? 75 : value.StartsWith("light") ? 71 : 73;
        if (value.Contains("sleet")) return value.StartsWith("heavy") ? 67 : 66;
        if (value.Contains("rain")) return value.StartsWith("heavy") ? 65 : value.StartsWith("light") ? 61 : 63;
        return value switch { "clearsky" => 0, "fair" => 1, "partlycloudy" => 2, "cloudy" => 3, "fog" => 45, _ => null };
    }
    public static WeatherData ParseOpenWeather(JsonElement current, JsonElement? forecast, City city, DateTimeOffset now)
    {
        var main = current.GetProperty("main");
        if (Number(main, "temp") is null) throw new JsonException("Missing current temperature");
        var observed = Unix(Number(current, "dt"));
        var condition = Items(Child(current, "weather")).FirstOrDefault();
        var sunrise = Unix(Number(Child(current, "sys"), "sunrise"));
        var sunset = Unix(Number(Child(current, "sys"), "sunset"));
        var hours = Hours(Items(forecast is { } f ? Child(f, "list") : default).Select(p => {
            var time = Unix(Number(p, "dt"));
            var rain = Number(Child(p, "rain"), "3h"); var snow = Number(Child(p, "snow"), "3h");
            return new WeatherHour(time ?? DateTimeOffset.MinValue, Number(Child(p, "main"), "temp"), Chance(Number(p, "pop") * 100), rain is not null || snow is not null ? Amount((rain ?? 0) + (snow ?? 0)) : null,
                OpenWeatherCode(Number(Items(Child(p, "weather")).FirstOrDefault(), "id")));
        }), now);
        // The current endpoint's temp_min/max describe nearby observations, not the day's extrema.
        var (low, high) = Range(hours, city, now);
        return new("ok", now, observed, city.Name, Number(main, "temp"), low, high, OpenWeatherCode(Number(condition, "id")),
            sunrise.HasValue && sunset.HasValue ? now >= sunrise && now < sunset : !Text(condition, "icon").EndsWith('n'),
            forecast.HasValue ? "OpenWeatherMap · 三小时预报；日范围为余下预报采样范围" : "OpenWeatherMap · 实况；预报暂不可用", hours, city.Timezone);
    }
    public static int? OpenWeatherCode(double? code) => code switch {
        >= 200 and <= 232 => 95, >= 300 and <= 321 => 53, 500 => 61, 501 => 63, >= 502 and <= 504 => 65, 511 => 66,
        520 => 80, 521 => 81, 522 or 531 => 82, 600 => 71, 601 => 73, 602 => 75, >= 611 and <= 616 => 66, >= 620 and <= 622 => 85,
        701 or 711 or 721 or 731 or 741 or 751 or 761 or 762 => 45, 771 or 781 => 95,
        800 => 0, 801 => 1, 802 => 2, 803 or 804 => 3, _ => null };
    public static WeatherData ParseWeatherApi(JsonElement root, City city, DateTimeOffset now)
    {
        var current = root.GetProperty("current");
        if (Number(current, "temp_c") is null) throw new JsonException("Missing current temperature");
        var days = Items(Child(Child(root, "forecast"), "forecastday")).ToArray();
        var day = days.FirstOrDefault(p => Text(p, "date") == TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(city.Timezone)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var hours = Hours(days.SelectMany(p => Items(Child(p, "hour"))).Select(p => new WeatherHour(Unix(Number(p, "time_epoch")) ?? DateTimeOffset.MinValue,
            Number(p, "temp_c"), Chance(new[] { Number(p, "chance_of_rain"), Number(p, "chance_of_snow") }.Max()), Amount(Number(p, "precip_mm")), WeatherApiCode(Number(Child(p, "condition"), "code")))), now);
        return new("ok", now, Unix(Number(current, "last_updated_epoch")), city.Name, Number(current, "temp_c"), Number(Child(day, "day"), "mintemp_c"), Number(Child(day, "day"), "maxtemp_c"),
            WeatherApiCode(Number(Child(current, "condition"), "code")), Number(current, "is_day") == 1, "WeatherAPI.com 天气预报", hours, city.Timezone);
    }
    public static int? WeatherApiCode(double? code) => code switch {
        1000 => 0, 1003 => 2, 1006 or 1009 => 3, 1030 or 1135 => 45, 1147 => 48,
        1063 or 1180 or 1183 => 61, 1186 or 1189 => 63, 1192 or 1195 => 65,
        1072 or 1168 or 1198 => 56, 1171 or 1201 => 57, 1150 or 1153 => 51,
        1066 or 1210 or 1213 => 71, 1216 or 1219 => 73, 1114 or 1117 or 1222 or 1225 => 75,
        1069 or 1204 or 1207 or 1237 or 1249 or 1252 or 1261 or 1264 => 66,
        1240 => 80, 1243 or 1246 => 82, 1255 => 85, 1258 => 86,
        1087 or 1273 or 1276 => 95, 1279 or 1282 => 96, _ => null };
}
