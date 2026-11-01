using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

/// <summary>
/// 周报的"到点了吗"判定。原来写在 Worker 里用 DateTimeOffset.UtcNow，只能靠改系统时间或等一周才验一次；
/// 抽成纯函数后按东八区 wall clock 直接测边界（19:59 / 20:00 / 周一）。
/// </summary>
public class WeeklyScheduleTests
{
    /// <summary>2026-11-01 是周日。</summary>
    private static long CnAt(string dateCn, int hour, int minute) =>
        TimeCn.DayStartUtcMs(dateCn) + (hour * 60 + minute) * 60_000L;

    [Fact]
    public void sunday_at_exactly_20_is_due()
        => WeeklySchedule.IsDue(CnAt("2026-11-01", 20, 0), DayOfWeek.Sunday, 20).Should().BeTrue();

    [Fact]
    public void sunday_one_minute_early_is_not_due()
        => WeeklySchedule.IsDue(CnAt("2026-11-01", 19, 59), DayOfWeek.Sunday, 20).Should().BeFalse();

    [Fact]
    public void sunday_late_evening_is_still_due_so_a_missed_tick_can_catch_up()
        => WeeklySchedule.IsDue(CnAt("2026-11-01", 23, 30), DayOfWeek.Sunday, 20).Should().BeTrue();

    [Fact]
    public void monday_evening_is_not_due()
        => WeeklySchedule.IsDue(CnAt("2026-11-02", 20, 0), DayOfWeek.Sunday, 20).Should().BeFalse();

    [Fact]
    public void judgement_follows_east_eight_not_utc()
    {
        // 周日 20:00 东八区 = 周日 12:00 UTC；如果按 UTC 判，这一刻会被算成"还没到点"
        var sundayNoonUtc = CnAt("2026-11-01", 20, 0);

        TimeCn.ToCn(sundayNoonUtc).Hour.Should().Be(20);
        WeeklySchedule.IsDue(sundayNoonUtc, DayOfWeek.Sunday, 20).Should().BeTrue();
    }
}
