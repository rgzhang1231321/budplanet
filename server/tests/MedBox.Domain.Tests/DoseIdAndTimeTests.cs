using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

public class DoseIdAndTimeTests
{
    [Fact]
    public void dose_id_is_pid_date_slot()
    {
        DoseId.Build("P001", "2026-10-30", 480).Should().Be("P001_2026-10-30_480");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(480)]
    [InlineData(1439)]
    public void slot_must_be_minute_of_day(int slot)
        => DoseId.Build("P1", "2026-10-30", slot).Should().EndWith($"_{slot}");

    [Theory]
    [InlineData(-1)]
    [InlineData(1440)]
    public void slot_out_of_range_is_rejected(int slot)
        => FluentActions.Invoking(() => DoseId.Build("P1", "2026-10-30", slot))
            .Should().Throw<ValidationException>();

    [Fact]
    public void slot_is_minute_not_index()
    {
        // 08:00 → 480，不是 0
        TimeCn.TryParseHhMm("08:00", out var slot).Should().BeTrue();
        slot.Should().Be(480);
        TimeCn.HhMm(1230).Should().Be("20:30");
    }

    [Fact]
    public void date_uses_east8_not_utc()
    {
        // 东八区 2026-10-30 00:30 = UTC 2026-10-29 16:30
        var utc = new DateTimeOffset(2026, 10, 29, 16, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        TimeCn.DateCn(utc).Should().Be("2026-10-30");
        TimeCn.MinuteOfDay(utc).Should().Be(30);
    }

    [Fact]
    public void slot_ts_is_day_start_plus_slot()
    {
        var dayStart = TimeCn.DayStartUtcMs("2026-10-30");
        TimeCn.SlotUtcMs("2026-10-30", 480).Should().Be(dayStart + 480 * 60_000);
        TimeCn.DateCn(TimeCn.SlotUtcMs("2026-10-30", 480)).Should().Be("2026-10-30");
    }

    [Fact]
    public void week_start_is_monday()
    {
        // 2026-10-30 是周五
        TimeCn.WeekStart("2026-10-30").Should().Be("2026-10-26");
        TimeCn.WeekStart("2026-10-26").Should().Be("2026-10-26");
        TimeCn.WeekStart("2026-11-01").Should().Be("2026-10-26"); // 周日归本周
    }
}
