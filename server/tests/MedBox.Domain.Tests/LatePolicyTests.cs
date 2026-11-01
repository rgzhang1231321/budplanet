using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

/// <summary>
/// is_late 的判定基线。断的是"基准是原定 slot 还是被 snooze 推后的 due_ts"这条规则：
/// 用 due_ts 时，老人点一次「稍后提醒」再打卡就永远不迟到，配色当场失真。
/// </summary>
public class LatePolicyTests
{
    private const string Date = "2026-11-01";
    private const int Slot = 480; // 08:00
    private const int ToleranceMin = 15;

    [Fact]
    public void taken_inside_the_tolerance_is_not_late()
    {
        var now = TimeCn.SlotUtcMs(Date, Slot) + ToleranceMin * 60_000L;

        LatePolicy.IsLate(Date, Slot, now, ToleranceMin).Should().BeFalse();
    }

    [Fact]
    public void taken_one_minute_past_the_tolerance_is_late()
    {
        var now = TimeCn.SlotUtcMs(Date, Slot) + (ToleranceMin + 1) * 60_000L;

        LatePolicy.IsLate(Date, Slot, now, ToleranceMin).Should().BeTrue();
    }

    [Fact]
    public void late_is_judged_by_the_original_slot_so_snooze_cannot_hide_it()
    {
        // snooze 把 due_ts 推到了 slot+30min，20 分钟后才打卡：
        // 拿 due_ts 比 → 「不晚」（20 < 30+15）；拿 slot 比 → 「晚」（20 > 15）。后者才对。
        var now = TimeCn.SlotUtcMs(Date, Slot) + 20 * 60_000L;

        LatePolicy.IsLate(Date, Slot, now, ToleranceMin).Should().BeTrue();
    }

    [Fact]
    public void tolerance_comes_from_config_not_from_the_calendar_day()
    {
        var now = TimeCn.SlotUtcMs(Date, Slot) + 40 * 60_000L;

        LatePolicy.IsLate(Date, Slot, now, 60).Should().BeFalse(); // 阈值放宽到 60 分钟
        LatePolicy.IsLate(Date, Slot, now, 15).Should().BeTrue();
    }
}
