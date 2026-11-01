namespace MedBox.Domain;

/// <summary>
/// 用药周期归一。
///
/// 之前这里的取值是断的：前端 chip 传 `cycle_30`，后端只认 `days_n`，
/// 于是「30 天」被静默存成 `long_term` —— 用户选了疗程，系统当成终身，永远不会结束。
/// 这类 bug 不报错、不崩，只表现为"设了也没用"，最难被发现。
///
/// 约定：**入参同时接受 `days_n` 与 `cycle_{N}`，出参统一回 `cycle_{N}`**（前端只认这个写法）。
/// </summary>
public static class PlanCycle
{
    public const string LongTerm = "long_term";
    public const string DaysN = "days_n";

    /// <summary>`cycle_30` 没带 cycleDays 时的兜底天数。</summary>
    public const int DefaultDays = 30;

    private const string Prefix = "cycle_";

    /// <summary>入参归一：(存储用的 type, 天数)。天数仅 days_n 有值。</summary>
    public static (string Type, int? Days) Parse(string? cycleType, int? cycleDays)
    {
        if (string.IsNullOrWhiteSpace(cycleType)) return (LongTerm, null);

        if (string.Equals(cycleType, DaysN, StringComparison.Ordinal))
            return (DaysN, cycleDays is > 0 ? cycleDays : DefaultDays);

        if (cycleType.StartsWith(Prefix, StringComparison.Ordinal)
            && int.TryParse(cycleType.AsSpan(Prefix.Length), out var parsed)
            && parsed > 0)
            return (DaysN, parsed);

        // 认不出的值一律长期：宁可"多提醒"，不要因为一个拼错的枚举就把药停掉
        return (LongTerm, null);
    }

    /// <summary>出参回前端认的写法。</summary>
    public static string ToWire(string? cycleType, int? cycleDays)
        => string.Equals(cycleType, DaysN, StringComparison.Ordinal)
            ? $"{Prefix}{cycleDays ?? DefaultDays}"
            : LongTerm;

    /// <summary>周期最后一天（含）；长期返回 null。</summary>
    public static string? EndDate(string startDate, int? days)
        => days is > 0
            ? DateTime.ParseExact(startDate, "yyyy-MM-dd", null).AddDays(days.Value - 1).ToString("yyyy-MM-dd")
            : null;
}
