using System.Globalization;

namespace WallpaperBridge;

/// <summary>CPAMP monitoring range presets, calendar boundaries, and event cursors.</summary>
public sealed record ActivityQuery(string Range = "7d", string TimeZone = "", long? FromMs = null, long? ToMs = null, long? BeforeMs = null, long? BeforeId = null)
{
    public static ActivityQuery Parse(IQueryCollection query)
    {
        long? Number(string key) => !query.ContainsKey(key) ? null : long.TryParse(query[key], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException("时间范围或分页参数无效");
        var value = new ActivityQuery(query["range"].FirstOrDefault() ?? "7d", query["timeZone"].FirstOrDefault() ?? "", Number("fromMs"), Number("toMs"), Number("beforeMs"), Number("beforeId"));
        value.Bounds(DateTimeOffset.UtcNow);
        return value;
    }

    public (DateTimeOffset From, DateTimeOffset To) Bounds(DateTimeOffset now)
    {
        if (Range is not ("today" or "yesterday" or "7d" or "14d" or "30d" or "all" or "custom")) throw new ArgumentException("不支持的时间范围");
        TimeZoneInfo zone;
        try { zone = string.IsNullOrEmpty(TimeZone) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(TimeZone); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("时区无效"); }
        var localDay = TimeZoneInfo.ConvertTime(now, zone).Date;
        DateTimeOffset Midnight(DateTime day) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day, DateTimeKind.Unspecified), zone));
        var today = Midnight(localDay);
        var bounds = Range switch {
            "today" => (today, now),
            "yesterday" => (Midnight(localDay.AddDays(-1)), today),
            "7d" => (today.AddDays(-6), now),
            "14d" => (today.AddDays(-13), now),
            "30d" => (today.AddDays(-29), now),
            _ => (DateTimeOffset.FromUnixTimeMilliseconds(1), now)
        };
        if (FromMs is not null || ToMs is not null) {
            if (FromMs is null or <= 0 || ToMs is null || ToMs <= FromMs || ToMs > 253402300799999) throw new ArgumentException("开始时间必须早于结束时间");
            bounds = (DateTimeOffset.FromUnixTimeMilliseconds(FromMs.Value), DateTimeOffset.FromUnixTimeMilliseconds(ToMs.Value));
        } else if (Range == "custom") throw new ArgumentException("请选择自定义起止时间");
        if ((BeforeMs is null) != (BeforeId is null) || BeforeId is <= 0 or > 9007199254740991 || BeforeMs is not null && (BeforeMs < bounds.Item1.ToUnixTimeMilliseconds() || BeforeMs >= bounds.Item2.ToUnixTimeMilliseconds())) throw new ArgumentException("分页游标无效");
        return bounds;
    }

    public string Label => Range switch { "today" => "今天", "yesterday" => "昨天", "7d" => "最近 7 天", "14d" => "最近 14 天", "30d" => "最近 30 天", "all" => "全部", _ => "自定义范围" };
}
