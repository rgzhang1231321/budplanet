namespace MedBox.Domain;

/// <summary>
/// 东八区时间口径：时刻一律 UTC 毫秒；自然日一律 "YYYY-MM-DD" 东八区。
/// 严禁用 DateTime.Now / ToLocalTime 推日期（服务器时区不可控）。
/// </summary>
public static class TimeCn
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    public static DateTimeOffset ToCn(long utcMs) => DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToOffset(Offset);

    public static string DateCn(long utcMs) => ToCn(utcMs).ToString("yyyy-MM-dd");

    public static int MinuteOfDay(long utcMs)
    {
        var t = ToCn(utcMs);
        return t.Hour * 60 + t.Minute;
    }

    /// <summary>某东八区自然日 00:00 的 UTC 毫秒。</summary>
    public static long DayStartUtcMs(string dateCn)
    {
        var d = DateTime.SpecifyKind(DateTime.ParseExact(dateCn, "yyyy-MM-dd", null), DateTimeKind.Unspecified);
        var cn = new DateTimeOffset(d, Offset);
        return cn.ToUnixTimeMilliseconds();
    }

    /// <summary>某日某 slot 的 UTC 毫秒。</summary>
    public static long SlotUtcMs(string dateCn, int slot) => DayStartUtcMs(dateCn) + slot * 60_000L;

    public static string HhMm(int slot) => $"{slot / 60:00}:{slot % 60:00}";

    public static bool TryParseHhMm(string? text, out int slot)
    {
        slot = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
        if (h is < 0 or > 23 || m is < 0 or > 59) return false;
        slot = h * 60 + m;
        return true;
    }

    /// <summary>本周一（东八区）。</summary>
    public static string WeekStart(string dateCn)
    {
        var d = DateTime.ParseExact(dateCn, "yyyy-MM-dd", null);
        var dow = (int)d.DayOfWeek; // 0=周日
        var diff = dow == 0 ? -6 : 1 - dow;
        return d.AddDays(diff).ToString("yyyy-MM-dd");
    }

    /// <summary>ISO 周键，例如 "2026-W44"。周报与 weekly_stats 都用它做幂等键。</summary>
    public static string WeekKey(string dateCn)
    {
        var d = DateTime.ParseExact(dateCn, "yyyy-MM-dd", null);
        var week = System.Globalization.ISOWeek.GetWeekOfYear(d);
        var year = System.Globalization.ISOWeek.GetYear(d);
        return $"{year}-W{week:00}";
    }

    /// <summary>
    /// 周区间短串，用于订阅消息的 time 字段：「10.26-11.01」。
    /// 微信 time 类型限 24 字符且不认区间符号以外的花哨写法，周报模板里这是唯一能放日期的地方。
    /// </summary>
    public static string WeekRangeText(string weekStart)
    {
        var s = DateTime.ParseExact(weekStart, "yyyy-MM-dd", null);
        var e = s.AddDays(6);
        return $"{s:MM.dd}-{e:MM.dd}";
    }

    /// <summary>某周结束（下周一 00:00 东八区）的 UTC 毫秒，用来判断"这份周报还新不新鲜"。</summary>
    public static long WeekEndUtcMs(string weekStart) => DayStartUtcMs(weekStart) + 7 * 86_400_000L;

    public static IEnumerable<string> WeekDates(string weekStart) =>
        Enumerable.Range(0, 7).Select(i => DateTime.ParseExact(weekStart, "yyyy-MM-dd", null).AddDays(i).ToString("yyyy-MM-dd"));
}
