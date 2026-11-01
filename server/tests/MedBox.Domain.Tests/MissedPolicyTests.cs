using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

public class MissedPolicyTests
{
    private const int Threshold = 60;

    [Fact]
    public void pending_becomes_missed_after_slot_plus_threshold()
    {
        var slotTs = TimeCn.SlotUtcMs("2026-10-30", 480); // 08:00
        MissedPolicy.ShouldBeMissed("pending", false, "2026-10-30", 480, slotTs + Threshold * 60_000L, Threshold)
            .Should().BeTrue();
    }

    [Fact]
    public void within_threshold_is_still_pending()
    {
        var slotTs = TimeCn.SlotUtcMs("2026-10-30", 480);
        MissedPolicy.ShouldBeMissed("pending", false, "2026-10-30", 480, slotTs + 59 * 60_000L, Threshold)
            .Should().BeFalse();
    }

    [Fact]
    public void snooze_does_not_move_the_missed_deadline()
    {
        // due_ts 被 snooze 推后了，但判定只认 slot —— 否则点一次"稍后提醒"就能把漏服推到明天
        var slotTs = TimeCn.SlotUtcMs("2026-10-30", 480);
        var nowAfterTwoSnooze = slotTs + 30 * 60_000L; // due_ts 变成 08:30，但 slot 仍是 08:00
        MissedPolicy.MissedAtUtcMs("2026-10-30", 480, Threshold).Should().Be(slotTs + 60 * 60_000L);
        MissedPolicy.ShouldBeMissed("pending", false, "2026-10-30", 480, nowAfterTwoSnooze, Threshold).Should().BeFalse();

        // 到 09:01 就该判漏服，与 snooze 次数无关
        MissedPolicy.ShouldBeMissed("pending", false, "2026-10-30", 480, slotTs + 61 * 60_000L, Threshold).Should().BeTrue();
    }

    [Theory]
    [InlineData("taken")]
    [InlineData("missed")]
    [InlineData("skipped")]
    public void only_pending_rows_are_touched(string status)
    {
        var slotTs = TimeCn.SlotUtcMs("2026-10-30", 480);
        MissedPolicy.ShouldBeMissed(status, false, "2026-10-30", 480, slotTs + 10 * 3600_000L, Threshold)
            .Should().BeFalse();
    }

    [Fact]
    public void corrected_rows_are_never_touched()
        => MissedPolicy.ShouldBeMissed("pending", true, "2026-10-30", 480, long.MaxValue, Threshold).Should().BeFalse();
}

public class DailyScheduleTests
{
    [Fact]
    public void next_run_today_when_not_passed()
    {
        var now = TimeCn.SlotUtcMs("2026-10-30", 9 * 60); // 09:00
        var next = DailySchedule.NextRunUtcMs(now, 0, 10);
        next.Should().Be(TimeCn.SlotUtcMs("2026-10-31", 10));
    }

    [Fact]
    public void next_run_tomorrow_when_passed()
    {
        var now = TimeCn.SlotUtcMs("2026-10-30", 11 * 60); // 11:00，已过 00:10
        var next = DailySchedule.NextRunUtcMs(now, 0, 10);
        next.Should().Be(TimeCn.SlotUtcMs("2026-10-31", 10));
    }

    [Fact]
    public void next_run_is_strictly_in_the_future()
    {
        var now = TimeCn.SlotUtcMs("2026-10-30", 10); // 恰好 00:10
        DailySchedule.NextRunUtcMs(now, 0, 10).Should().BeGreaterThan(now);
    }
}
