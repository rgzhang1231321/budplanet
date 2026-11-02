using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

/// <summary>
/// week_key 口径的一致性：<see cref="TimeCn.WeekDates"/> 的每一天都必须落在同一个
/// <see cref="TimeCn.WeekKey"/> 里。周报聚合从"due_date IN (7 天)"改成"week_key = ?"，
/// 这个断言就是两种谓词等价的前提 —— 不成立就不能改谓词（否则会漏一天或多吃一天）。
/// </summary>
public class TimeCnWeekKeyTests
{
    /// <summary>跨够多的周界样本：ISO 周 1 从"含 1 月 4 日那一周"起算，年初/年末最容易错位。</summary>
    private static readonly string[] WeekStarts =
    [
        "2024-01-01", // 周一，恰是 2024-W01
        "2025-12-29", // 周一，属于 2026-W01（ISO 年比日历年早）
        "2026-01-05",
        "2026-10-26",
        "2026-11-02",
        "2026-12-28", // 周一，属于 2026-W53（长 ISO 年）
    ];

    [Theory]
    [InlineData("2024-01-01")]
    [InlineData("2025-12-29")]
    [InlineData("2026-01-05")]
    [InlineData("2026-10-26")]
    [InlineData("2026-11-02")]
    [InlineData("2026-12-28")]
    public void 一周七天共用同一个_week_key(string weekStart)
    {
        var expected = TimeCn.WeekKey(weekStart);

        foreach (var d in TimeCn.WeekDates(weekStart))
            TimeCn.WeekKey(d).Should().Be(expected, $"因为 {d} 属于以 {weekStart} 起算的那一周");
    }

    [Fact]
    public void week_start_本身就是周一()
    {
        foreach (var weekStart in WeekStarts)
        {
            var d = DateTime.ParseExact(weekStart, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            d.DayOfWeek.Should().Be(DayOfWeek.Monday, $"WeekStart({weekStart}) 必须返回周一");
        }
    }

    [Fact]
    public void week_key_形如四位年_W两位周()
    {
        foreach (var weekStart in WeekStarts)
            TimeCn.WeekKey(weekStart).Should().MatchRegex(@"^20\d{2}-W(0[1-9]|[1-4]\d|5[0-3])$");
    }
}
