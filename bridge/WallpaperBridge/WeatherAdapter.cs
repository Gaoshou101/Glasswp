using System.Text.Json;

namespace WallpaperBridge;

public static class WeatherAdapter
{
    public static WeatherData Parse(JsonElement root, City city, DateTimeOffset fetchedAt)
    {
        var current = root.GetProperty("current");
        var daily = root.GetProperty("daily");
        double? First(string name) => At(daily, name, 0);
        var hours = new List<WeatherHour>();
        if (root.TryGetProperty("hourly", out var hourly) && hourly.TryGetProperty("time", out var times) && times.ValueKind == JsonValueKind.Array)
        {
            for (var i = 0; i < Math.Min(times.GetArrayLength(), 72); i++)
            {
                var unix = At(hourly, "time", i);
                // A malformed timestamp must not discard usable current weather.
                if (unix is null or < 0 or > 253402300799) continue;
                var time = DateTimeOffset.FromUnixTimeSeconds((long)unix.Value);
                if (time <= fetchedAt.AddHours(-1) || time > fetchedAt.AddHours(24)) continue;
                var probability = At(hourly, "precipitation_probability", i);
                if (probability.HasValue) probability = Math.Clamp(probability.Value, 0, 100);
                var amount = At(hourly, "precipitation", i);
                if (amount < 0) amount = null;
                hours.Add(new(time, At(hourly, "temperature_2m", i), probability, amount, Code(At(hourly, "weather_code", i))));
            }
        }
        var observed = QuotaAdapter.Number(current, "time");
        return new("ok", fetchedAt, observed is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)observed) : null,
            city.Name, QuotaAdapter.Number(current, "temperature_2m"), First("temperature_2m_min"), First("temperature_2m_max"),
            Code(QuotaAdapter.Number(current, "weather_code")), QuotaAdapter.Number(current, "is_day") == 1, "Open-Meteo 天气模型",
            hours.OrderBy(h => h.Time).DistinctBy(h => h.Time).Take(25).ToArray(), city.Timezone);
    }
    private static int? Code(double? value) => value is >= 0 and <= 99 ? (int)value.Value : null;
    private static double? At(JsonElement data, string name, int index)
    {
        if (!data.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array || index >= values.GetArrayLength()) return null;
        var value = values[index];
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    }
}
