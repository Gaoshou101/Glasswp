using System.Net;
using System.Text.Json;
using WallpaperBridge;

static class WeatherProviderChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var city = new City("test", "Test", 31.3, 120.5, "Asia/Shanghai");
        var cfg = BridgeConfig.Default with { City = city, QuotaRefreshEnabled = false };
        var observed = now.ToUnixTimeSeconds();
        var later = now.AddHours(3).ToUnixTimeSeconds();
        using var met = JsonDocument.Parse(JsonSerializer.Serialize(new { properties = new { timeseries = new[] {
            new { time = now.ToString("O"), data = new { instant = new { details = new { air_temperature = 21.5 } }, next_1_hours = new { summary = new { symbol_code = "lightrain_night" }, details = new { precipitation_amount = 1.2, probability_of_precipitation = 130 } } } },
            new { time = now.AddHours(1).ToString("O"), data = new { instant = new { details = new { air_temperature = 22.5 } }, next_1_hours = new { summary = new { symbol_code = "partlycloudy_day" }, details = new { precipitation_amount = 0.0, probability_of_precipitation = 0 } } } }
        } } }));
        var metParsed = WeatherProviders.ParseMet(met.RootElement, city, now);
        check(metParsed.Temperature == 21.5 && metParsed.Code == 61 && !metParsed.IsDay && metParsed.Hourly?.Length == 2 && metParsed.Hourly[0].PrecipitationProbability == 100,
            "MET forecast maps symbols, local current point and clamped precipitation without assuming observations");
        check(WeatherProviders.MetCode("heavysnowandthunder_day") == 96 && WeatherProviders.MetCode("sleetshowers_night") == 66 && WeatherProviders.MetCode("new_unknown_day") is null,
            "MET unfamiliar codes remain unknown and frozen precipitation is not displayed as rain");
        try { WeatherProviders.ParseMet(met.RootElement, city, now.AddHours(5)); check(false, "expired MET forecast must fail"); }
        catch (JsonException) { check(true, "MET forecast outside the current hour never receives a fresh successful status"); }
        using var current = JsonDocument.Parse(JsonSerializer.Serialize(new {
            dt = observed, main = new { temp = 20, temp_min = -100, temp_max = 100 },
            weather = new[] { new { id = 800, icon = "01d" } }, sys = new { sunrise = observed - 3600, sunset = observed + 3600 }
        }));
        using var forecast = JsonDocument.Parse(JsonSerializer.Serialize(new { list = new[] {
            new { dt = later, main = new { temp = 24 }, weather = new[] { new { id = 601 } }, pop = .6, snow = new Dictionary<string, double> { ["3h"] = 2.5 } }
        } }));
        var openWeather = WeatherProviders.ParseOpenWeather(current.RootElement, forecast.RootElement, city, now);
        check(openWeather.Temperature == 20 && openWeather.IsDay && openWeather.Hourly?[0] is { Code: 73, PrecipitationProbability: 60, PrecipitationMm: 2.5 },
            "OpenWeatherMap three-hour samples map probability, snow amount and WMO codes");
        check(openWeather.Low != -100 && openWeather.High != 100, "OpenWeatherMap surrounding station temp_min/max are never mislabeled as daily extrema");
        var withoutForecast = WeatherProviders.ParseOpenWeather(current.RootElement, null, city, now);
        check(withoutForecast.Temperature == 20 && withoutForecast.Hourly?.Length == 0 && withoutForecast.Low is null && withoutForecast.High is null,
            "OpenWeatherMap preserves current conditions when no forecast is available");
        var localDay = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(city.Timezone)).ToString("yyyy-MM-dd");
        using var weatherApi = JsonDocument.Parse(JsonSerializer.Serialize(new {
            current = new { last_updated_epoch = observed, temp_c = 25, is_day = 0, condition = new { code = 1276 } },
            forecast = new { forecastday = new[] { new { date = localDay, day = new { mintemp_c = 18, maxtemp_c = 28 }, hour = new[] {
                new { time_epoch = observed, temp_c = 25, chance_of_rain = 30, chance_of_snow = 70, precip_mm = .5, condition = new { code = 1255 } }
            } } } }
        }));
        var apiParsed = WeatherProviders.ParseWeatherApi(weatherApi.RootElement, city, now);
        check(apiParsed is { Temperature: 25, Low: 18, High: 28, Code: 95, IsDay: false } && apiParsed.Hourly?[0] is { Code: 85, PrecipitationProbability: 70 },
            "WeatherAPI maps actual daily extrema, independent day state and either rain or snow probability");
        check(WeatherProviders.WeatherApiCode(9999) is null && WeatherProviders.OpenWeatherCode(9999) is null, "unknown provider weather codes never become a false sunny condition");
        foreach (var invalid in new[] { new WeatherSettings("wrong"), new WeatherSettings("openweathermap", "\ninvalid") })
        {
            try { WeatherProviders.ValidateSettings(invalid); check(false, "invalid weather settings must fail"); }
            catch (ArgumentException) { check(true, "invalid weather source or missing/unsafe credentials are rejected"); }
        }
        check(WeatherProviders.CacheIdentity(cfg) != WeatherProviders.CacheIdentity(cfg with { City = city with { Latitude = 32 } }) &&
            WeatherProviders.CacheIdentity(cfg) != WeatherProviders.CacheIdentity(cfg with { Weather = new("met-no") }), "weather cache identity includes exact coordinates and provider");

        var upstreamReads = 0;
        using (var http = new HttpClient(new StubHttpHandler((request, _) => {
            upstreamReads++;
            check(request.RequestUri is { Scheme: "https", Host: "api.met.no" } && request.Headers.UserAgent.ToString().Contains("PortraitWallpaperDashboard/0.1"), "MET request has a fixed HTTPS host and identifying user agent");
            var response = StubHttpHandler.Json(met.RootElement); response.Content.Headers.Expires = now.AddHours(1);
            return Task.FromResult(response);
        })))
        {
            var provider = new WeatherProviders();
            var first = await provider.FetchAsync(http, cfg with { Weather = new("met-no") }, CancellationToken.None);
            await provider.FetchAsync(http, cfg with { Weather = new("met-no") }, CancellationToken.None);
            check(upstreamReads == 1 && first.Provider == "met-no" && first.CacheIdentity == WeatherProviders.CacheIdentity(cfg with { Weather = new("met-no") }), "MET advertized expiry avoids repeat upstream calls and snapshots carry only nonsecret source identity");
        }
        var hosts = new List<string>();
        using (var http = new HttpClient(new StubHttpHandler((request, _) => {
            hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath.EndsWith("forecast")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(StubHttpHandler.Json(current.RootElement));
        })))
        {
            var fetched = await new WeatherProviders().FetchAsync(http, cfg with { Weather = new("openweathermap", "test-only-do-not-leak") }, CancellationToken.None);
            check(fetched.Temperature == 20 && fetched.Provider == "openweathermap" && hosts.Count == 2 && hosts.All(h => h == "api.openweathermap.org"), "OpenWeatherMap only sends credentials to its fixed host and a forecast outage keeps current conditions");
            check(!JsonSerializer.Serialize(fetched, PrivateStore.Json).Contains("test-only-do-not-leak"), "API keys never appear in display weather snapshots");
        }
        using (var http = new HttpClient(new StubHttpHandler((request, _) => {
            check(request.RequestUri is { Scheme: "https", Host: "api.weatherapi.com" } && request.RequestUri.AbsolutePath == "/v1/forecast.json", "WeatherAPI uses its fixed HTTPS forecast endpoint");
            return Task.FromResult(StubHttpHandler.Json(weatherApi.RootElement));
        })))
        {
            var fetched = await new WeatherProviders().FetchAsync(http, cfg with { Weather = new("weatherapi", "test-only") }, CancellationToken.None);
            check(fetched.Provider == "weatherapi" && fetched.Temperature == 25, "WeatherAPI network response enters the same display contract");
        }
        var temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wallpaper-weather-" + Guid.NewGuid()));
        try
        {
            var store = new PrivateStore(temp);
            store.Save(cfg);
            var hub = new DataHub(store);
            store.Save(cfg with { Weather = new("weatherapi") });
            using (var unconfigured = new RemoteSources(store, hub, new StubHttpHandler((_, _) => throw new Exception("missing key must not start network request"))))
            {
                await unconfigured.ReadWeatherOnce(CancellationToken.None);
                check(hub.Snapshot().Weather is { Status: "unconfigured", Temperature: null }, "cleared required API key clears old weather and reports unconfigured without network access");
            }
            store.Save(cfg);
            var existing = apiParsed with { Provider = "open-meteo", CacheIdentity = WeatherProviders.CacheIdentity(cfg) };
            hub.Weather(existing);
            using (var failed = new RemoteSources(store, hub, new StubHttpHandler((_, _) => throw new HttpRequestException("test-only"))))
            {
                await failed.ReadWeatherOnce(CancellationToken.None);
                check(hub.Snapshot().Weather is { Status: "stale", Temperature: 25 } && hub.Snapshot().Weather.FetchedAt == existing.FetchedAt,
                    "failed weather refresh preserves source-matched values and original evidence age");
                store.Save(cfg with { Weather = new("met-no") });
                await failed.ReadWeatherOnce(CancellationToken.None);
                check(hub.Snapshot().Weather is { Status: "offline", Temperature: null, Provider: "met-no" }, "failed source switch never shows another provider's old temperature");
            }
            store.Save(cfg);
            store.WriteCache("weather-cache", existing);
            check(new DataHub(store).Snapshot().Weather.Status == "stale", "source-matched encrypted weather cache survives restart");
            store.Save(cfg with { City = city with { Latitude = city.Latitude + 1 } });
            check(new DataHub(store).Snapshot().Weather.Temperature is null, "same-name city with different coordinates cannot reuse weather cache");
            store.Save(cfg with { Weather = new("met-no") });
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var remote = new RemoteSources(store, hub, new StubHttpHandler(async (_, token) => {
                pending.TrySetResult(); await release.Task.WaitAsync(token); return StubHttpHandler.Json(met.RootElement);
            })))
            {
                var read = remote.ReadWeatherOnce(CancellationToken.None);
                await pending.Task.WaitAsync(TimeSpan.FromSeconds(3));
                store.Save(cfg with { Weather = new("weatherapi", "test-only") });
                hub.Weather(WeatherProviders.EmptyFor(store.Config));
                release.TrySetResult(); await read;
                check(hub.Snapshot().Weather is { Temperature: null, Provider: "weatherapi" }, "late old-provider response cannot overwrite the newly selected source");
            }
        }
        finally
        {
            if (!temp.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup");
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }
}
