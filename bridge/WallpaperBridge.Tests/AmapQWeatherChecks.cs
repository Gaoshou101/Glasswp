using System.IO.Compression;
using System.Net;
using System.Text.Json;
using WallpaperBridge;

static class AmapQWeatherChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var city = new City("test", "配置城市", 31.30408, 120.59538, "Asia/Shanghai");
        var cfg = BridgeConfig.Default with { City = city, QuotaRefreshEnabled = false };
        var chinaNow = now.ToOffset(TimeSpan.FromHours(8));
        using var live = JsonDocument.Parse(JsonSerializer.Serialize(new { status = "1", infocode = "10000", lives = new[] {
            new { adcode = "320500", city = "苏州市", province = "江苏", weather = "小雨", temperature = "16", temperature_float = "16.5", reporttime = "2026-11-10 11:12:13" }
        } }));
        using var daily = JsonDocument.Parse(JsonSerializer.Serialize(new { status = "1", infocode = "10000", forecasts = new[] {
            new { adcode = "320500", casts = new[] { new { date = chinaNow.ToString("yyyy-MM-dd"), daytemp = "23", nighttemp = "15" } } }
        } }));
        var amap = WeatherProviders.ParseAmap(live.RootElement, daily.RootElement, "320500", now);
        check(amap is { Temperature: 16.5, Low: 15, High: 23, Code: 61, Timezone: "Asia/Shanghai" } && amap.Hourly?.Length == 0,
            "Amap string-valued observations and daily day/night temperatures map without invented hourly forecasts");
        check(amap.City == "苏州市 · 江苏 (320500)" && amap.ObservedAt == DateTimeOffset.Parse("2026-11-10T03:12:13Z"), "Amap returns its actual city and parses reporttime with UTC+08:00");
        check(WeatherProviders.ParseAmap(live.RootElement, null, "320500", now) is { Temperature: 16.5, Low: null, High: null }, "Amap observation survives absent daily forecast");
        check(WeatherProviders.AmapCode("雷阵雨并伴有冰雹") == 96 && WeatherProviders.AmapCode("雨夹雪") == 66 && WeatherProviders.AmapCode("霾") is null,
            "Amap hail and frozen rain map conservatively and unsupported haze is not called sunny");
        foreach (var mode in new[] { "bad-status", "bad-code", "wrong-city", "missing-temp" })
        {
            var value = JsonSerializer.SerializeToElement(new { status = mode == "bad-status" ? "0" : "1", infocode = mode == "bad-code" ? "10001" : "10000", lives = new[] {
                new { adcode = mode == "wrong-city" ? "110100" : "320500", city = "苏州市", weather = "晴", temperature = mode == "missing-temp" ? "--" : "20" }
            } });
            var rejected = false;
            try { WeatherProviders.ParseAmap(value, null, "320500", now); } catch (JsonException) { rejected = true; }
            check(rejected, "Amap rejects " + mode + " response instead of showing successful weather");
        }
        using var current = JsonDocument.Parse("""{"metadata":{"attributions":["https://developer.qweather.com/attribution.html"]},"condition":{"text":"小雨","code":"305"},"temperature":{"value":18.5,"unit":"°C"}}""");
        using var qDaily = JsonDocument.Parse(JsonSerializer.Serialize(new { days = new[] {
            new { forecastStartTime = now.AddHours(-3).ToString("O"), forecastEndTime = now.AddHours(21).ToString("O"),
                astro = new { sunrise = now.AddHours(-2).ToString("O"), sunset = now.AddHours(8).ToString("O") }, temperatureMin = new { value = 12.0 }, temperatureMax = new { value = 21.0 } }
        } }));
        using var hourly = JsonDocument.Parse(JsonSerializer.Serialize(new { hours = new[] {
            new { forecastTime = now.AddHours(1).ToString("O"), condition = new { code = "400" }, temperature = new { value = 17.0 }, precipitation = new { amount = new { value = .8 }, probability = .65 } }
        } }));
        var qParsed = WeatherProviders.ParseQWeather(current.RootElement, qDaily.RootElement, hourly.RootElement, city, now);
        check(qParsed is { Temperature: 18.5, Low: 12, High: 21, Code: 61, IsDay: true, ObservedAt: null } &&
            qParsed.Hourly?[0] is { Code: 71, Temperature: 17, PrecipitationMm: .8, PrecipitationProbability: 65 }, "QWeather v1 maps nested values, probability fractions, WMO codes and astro without fabricated observation time");
        check(qParsed.Message.Contains("https://developer.qweather.com/attribution.html"), "QWeather mandatory attribution is retained in the source description");
        check(WeatherProviders.QWeatherCode(304) == 96 && WeatherProviders.QWeatherCode(404) == 66 && WeatherProviders.QWeatherCode(999) is null, "QWeather unknown weather codes stay unknown");
        foreach (var value in new[] { "", "https://example.qweatherapi.com", "example.qweatherapi.com/path", "example.qweatherapi.com:443", "127.0.0.1", "example..re.qweatherapi.com", "example.-re.qweatherapi.com", "example.re-.qweatherapi.com", "qweatherapi.com", "example.re.qweatherapi.com.evil.test", "example.qweatherapi.com.evil.test", "user@example.qweatherapi.com", "-example.qweatherapi.com", "example-.qweatherapi.com", "example.qweatherapi.com\n", "例子.qweatherapi.com" })
            check(!WeatherProviders.ValidQWeatherHost(value), "QWeather rejects non-host or unsafe host: " + value.Replace("\n", "\\n"));
        check(WeatherProviders.ValidQWeatherHost("example-1.qweatherapi.com") && !WeatherProviders.ValidAmapCityCode("３２０５００") && WeatherProviders.ValidAmapCityCode("320500"), "provider host and city code accept only the strict ASCII formats");
        check(WeatherProviders.ValidQWeatherHost("demo.re.qweatherapi.com"), "QWeather regional multi-label API hosts are accepted");
        var longest = new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 45) + ".qweatherapi.com";
        check(longest.Length == 253 && WeatherProviders.ValidQWeatherHost(longest) && !WeatherProviders.ValidQWeatherHost("a" + longest), "QWeather total DNS host length is bounded at 253");
        check(!WeatherProviders.ValidQWeatherHost(new string('a', 64) + ".re.qweatherapi.com"), "QWeather every regional host label is bounded at 63");
        var amapCfg = cfg with { Weather = new("amap", "amap-fixture-key", CityCode: "320500") };
        var qCfg = cfg with { Weather = new("qweather", "qweather-fixture-key", ApiHost: "fixture.re.qweatherapi.com") };
        check(WeatherProviders.CacheIdentity(qCfg) != WeatherProviders.CacheIdentity(qCfg with { Weather = qCfg.Weather! with { ApiHost = "other.qweatherapi.com" } }) &&
            WeatherProviders.CacheIdentity(amapCfg) != WeatherProviders.CacheIdentity(amapCfg with { Weather = amapCfg.Weather! with { CityCode = "110100" } }) &&
            WeatherProviders.CacheIdentity(qCfg) == WeatherProviders.CacheIdentity(qCfg with { Weather = qCfg.Weather! with { ApiKey = "another-fixture" } }), "cache identity separates API hosts and adcodes without embedding the secret");
        var amapReads = 0;
        using (var http = new HttpClient(new StubHttpHandler((request, _) => {
            amapReads++;
            check(request.RequestUri is { Scheme: "https", Host: "restapi.amap.com", AbsolutePath: "/v3/weather/weatherInfo" } &&
                request.RequestUri.Query.Contains("city=320500") && request.RequestUri.Query.Contains("key=amap-fixture-key") && !request.Headers.Contains("X-QW-Api-Key"), "Amap fixed endpoint receives only its own key and explicit adcode");
            return Task.FromResult(StubHttpHandler.Json(request.RequestUri!.Query.Contains("extensions=base") ? live.RootElement : daily.RootElement));
        })))
        {
            var result = await new WeatherProviders().FetchAsync(http, amapCfg, CancellationToken.None);
            check(amapReads == 2 && result is { Provider: "amap", Temperature: 16.5, Low: 15 }, "Amap real request orchestration reads both current and daily endpoints");
            check(!JsonSerializer.Serialize(result, PrivateStore.Json).Contains("amap-fixture-key"), "Amap key never reaches wallpaper snapshot");
        }
        using (var http = new HttpClient(new StubHttpHandler((request, _) => Task.FromResult(request.RequestUri!.Query.Contains("extensions=base") ? StubHttpHandler.Json(live.RootElement) : new HttpResponseMessage(HttpStatusCode.Forbidden)))))
        {
            check((await new WeatherProviders().FetchAsync(http, amapCfg, CancellationToken.None)) is { Temperature: 16.5, Low: null, High: null }, "Amap forecast failure preserves usable current observations");
        }
        foreach (var failure in new[] { "none", "daily", "hourly", "both" })
        {
            var reads = 0;
            using var http = new HttpClient(new StubHttpHandler((request, _) => {
                reads++;
                check(request.RequestUri is { Scheme: "https", Host: "fixture.re.qweatherapi.com" } && request.RequestUri.AbsolutePath.EndsWith("/31.30/120.60") &&
                    !request.RequestUri.Query.Contains("key") && request.Headers.GetValues("X-QW-Api-Key").Single() == "qweather-fixture-key" && request.Headers.AcceptEncoding.Any(v => v.Value == "gzip"),
                    "QWeather v1 uses rounded latitude/longitude, compressed transport and a header-only host-scoped key");
                var isDaily = request.RequestUri!.AbsolutePath.Contains("/daily/");
                var isHourly = request.RequestUri.AbsolutePath.Contains("/hourly/");
                if (isDaily && failure is "daily" or "both" || isHourly && failure is "hourly" or "both") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                return Task.FromResult(Gzip(isDaily ? qDaily.RootElement : isHourly ? hourly.RootElement : current.RootElement));
            }));
            var fetched = await new WeatherProviders().FetchAsync(http, qCfg, CancellationToken.None);
            check(reads == 3 && fetched.Provider == "qweather" && fetched.Temperature == 18.5 &&
                fetched.Low == (failure is "daily" or "both" ? null : 12) && fetched.Hourly?.Length == (failure is "hourly" or "both" ? 0 : 1), "QWeather gzip responses and independent forecast downgrade: " + failure);
            check(!JsonSerializer.Serialize(fetched, PrivateStore.Json).Contains("qweather-fixture-key"), "QWeather API key is absent from returned weather");
        }
        foreach (var error in new[] { "{\"code\":\"401\"}", "{\"error\":{\"status\":403}}", "{\"condition\":{\"code\":\"100\"}}" })
        {
            using var http = new HttpClient(new StubHttpHandler((_, _) => Task.FromResult(StubHttpHandler.Json(JsonSerializer.Deserialize<JsonElement>(error)))));
            var rejected = false;
            try { await new WeatherProviders().FetchAsync(http, qCfg, CancellationToken.None); } catch (JsonException) { rejected = true; }
            check(rejected, "QWeather error codes and missing current temperature are never treated as successful weather");
        }
        using (var http = new HttpClient(new StubHttpHandler((_, _) => {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4, 5]) };
            response.Content.Headers.ContentEncoding.Add("gzip"); return Task.FromResult(response);
        })))
        {
            var rejected = false;
            try { await new WeatherProviders().FetchAsync(http, qCfg, CancellationToken.None); } catch (JsonException) { rejected = true; }
            check(rejected, "corrupt QWeather gzip is a recoverable source failure instead of a background worker crash");
        }
        using (var http = new HttpClient(new StubHttpHandler((_, _) => throw new Exception("invalid host must never reach network"))))
        {
            var rejected = false;
            try { await new WeatherProviders().FetchAsync(http, qCfg with { Weather = qCfg.Weather! with { ApiHost = "127.0.0.1" } }, CancellationToken.None); } catch (ArgumentException) { rejected = true; }
            check(rejected, "unsafe QWeather API host is rejected before attaching a credential or sending a request");
        }
        var temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wallpaper-amap-qweather-" + Guid.NewGuid()));
        try
        {
            var store = new PrivateStore(temp);
            store.Save(qCfg);
            var cached = qParsed with { Provider = "qweather", CacheIdentity = WeatherProviders.CacheIdentity(qCfg) };
            store.WriteCache("weather-cache", cached);
            check(new DataHub(store).Snapshot().Weather.Temperature == 18.5, "QWeather matching-host encrypted cache loads as stale weather");
            store.Save(qCfg with { Weather = qCfg.Weather! with { ApiHost = "other.qweatherapi.com" } });
            check(new DataHub(store).Snapshot().Weather.Temperature is null, "changing QWeather API host rejects the former host cache");
            store.Save(amapCfg);
            store.WriteCache("weather-cache", amap with { Provider = "amap", CacheIdentity = WeatherProviders.CacheIdentity(amapCfg) });
            store.Save(amapCfg with { Weather = amapCfg.Weather! with { CityCode = "110100" } });
            check(new DataHub(store).Snapshot().Weather.Temperature is null, "changing Amap adcode rejects the former city cache");
            foreach (var value in new[] { amapCfg, qCfg })
            {
                store.Save(value with { Weather = value.Weather! with { ApiKey = null } });
                var hub = new DataHub(store);
                using var remote = new RemoteSources(store, hub, new StubHttpHandler((_, _) => throw new Exception("missing credential must never start network")));
                await remote.ReadWeatherOnce(CancellationToken.None);
                check(hub.Snapshot().Weather is { Status: "unconfigured", Temperature: null }, "cleared " + value.Weather!.Provider + " key clears weather without network access");
            }
        }
        finally
        {
            if (!temp.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup");
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }
    private static HttpResponseMessage Gzip(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) JsonSerializer.Serialize(gzip, value);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) };
        response.Content.Headers.ContentEncoding.Add("gzip");
        return response;
    }
}
