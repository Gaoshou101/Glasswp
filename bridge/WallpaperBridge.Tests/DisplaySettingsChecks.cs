using System.Net;
using System.Text.Json;
using WallpaperBridge;

internal static class DisplaySettingsChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        ClockSettings.Validate(ClockSettings.Default);
        ClockSettings.Validate(ClockSettings.Default with { Timezone = "UTC", WorldZones = [] });
        foreach (var value in new[] {
            ClockSettings.Default with { Timezone = "Invalid/Zone" },
            ClockSettings.Default with { Timezone = "China Standard Time" },
            ClockSettings.Default with { OffsetSeconds = double.NaN },
            ClockSettings.Default with { OffsetSeconds = 86401 },
            ClockSettings.Default with { WorldZones = ["UTC", "UTC"] },
            ClockSettings.Default with { WorldZones = ["Invalid/Zone"] },
            ClockSettings.Default with { WorldZones = null! }
        }) {
            var rejected = false;
            try { ClockSettings.Validate(value); } catch (ArgumentException) { rejected = true; }
            check(rejected, "clock config rejects invalid timezone, calibration or world list");
        }
        var original = new WeatherSettings("weatherapi", "fixture-secret");
        check(new WeatherSettingsInput("weatherapi").Apply(original).ApiKey == original.ApiKey, "same provider blank key preserves encrypted credential");
        var missingKeyRejected = false;
        try { new WeatherSettingsInput("openweathermap").Apply(original); } catch (ArgumentException) { missingKeyRejected = true; }
        check(missingKeyRejected, "provider switch cannot forward another provider's key");
        check(new WeatherSettingsInput("weatherapi", ClearApiKey: true).Apply(original).ApiKey is null, "explicit weather key clearing is respected");
        check(new WeatherSettingsInput("open-meteo", "unused").Apply(original).ApiKey is null, "keyless sources discard irrelevant credential");
        var qweather = new WeatherSettings("qweather", "fixture-host-key", "original.qweatherapi.com");
        check(new WeatherSettingsInput("qweather", ApiHost: "ORIGINAL.qweatherapi.com").Apply(qweather).ApiKey == qweather.ApiKey, "normalized identical QWeather host preserves its credential");
        var changedHostRejected = false;
        try { new WeatherSettingsInput("qweather", ApiHost: "changed.qweatherapi.com").Apply(qweather); }
        catch (ArgumentException) { changedHostRejected = true; }
        check(changedHostRejected, "QWeather host change cannot silently forward the previous host credential");
        check(new WeatherSettingsInput("qweather", "new-fixture-key", ApiHost: "changed.qweatherapi.com").Apply(qweather).ApiKey == "new-fixture-key", "QWeather host change accepts an explicitly supplied replacement key");
        var legacy = JsonSerializer.Serialize(BridgeConfig.Default, PrivateStore.Json);
        check(JsonSerializer.Deserialize<BridgeConfig>(legacy, PrivateStore.Json)?.Clock is null, "legacy config keeps local display clock until admin saves it");

        var now = DateTimeOffset.UtcNow;
        using var sync = new DisplayTimeSync(new Handler(req => {
            check(req.RequestUri?.Scheme == "https" && req.RequestUri.Host == "www.cloudflare.com" && req.Method == HttpMethod.Head,
                "display time calibration uses a fixed HTTPS endpoint");
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Date = now.AddSeconds(30);
            return response;
        }));
        var result = await sync.Measure(CancellationToken.None);
        check(result.OffsetSeconds is > 25 and < 31 && result.RoundTripMs >= 0, "network calibration computes a display offset with round trip correction");
        foreach (var mode in new[] { "missing", "cached", "excessive", "redirect" }) {
            using var invalid = new DisplayTimeSync(new Handler(_ => {
                var response = new HttpResponseMessage(mode == "redirect" ? HttpStatusCode.Found : HttpStatusCode.OK);
                if (mode != "missing") response.Headers.Date = now.AddDays(mode == "excessive" ? 3 : 0);
                if (mode == "cached") response.Headers.Age = TimeSpan.FromSeconds(20);
                return response;
            }));
            var rejected = false;
            try { await invalid.Measure(CancellationToken.None); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) { rejected = true; }
            check(rejected, "display calibration rejects " + mode + " response");
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
}
