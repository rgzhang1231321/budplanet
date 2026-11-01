using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

public class DoseTransitionsTests
{
    [Theory]
    [InlineData(DoseStatus.Pending, DoseStatus.Taken)]
    [InlineData(DoseStatus.Pending, DoseStatus.Skipped)]
    [InlineData(DoseStatus.Pending, DoseStatus.Missed)]
    public void pending_can_go_to_any_terminal_state(DoseStatus from, DoseStatus to)
        => DoseTransitions.CanSubmit(from, to, isCorrected: false).Should().BeTrue();

    [Fact]
    public void system_missed_can_be_rescued_to_taken()
    {
        // 吃了没打卡是这个产品最高频的假阳性，必须能救回
        DoseTransitions.CanSubmit(DoseStatus.Missed, DoseStatus.Taken, isCorrected: false).Should().BeTrue();
    }

    [Fact]
    public void manually_corrected_missed_cannot_be_flipped_back()
    {
        DoseTransitions.CanSubmit(DoseStatus.Missed, DoseStatus.Taken, isCorrected: true).Should().BeFalse();
        DoseTransitions.CanCorrect(DoseStatus.Missed, DoseStatus.Taken, isCorrected: true).Should().BeFalse();
    }

    [Fact]
    public void taken_cannot_jump_back_to_pending_by_submit()
    {
        // 契约没有 taken→pending：撤销只能靠"窗口内暂缓上行"，不能靠改状态
        DoseTransitions.CanSubmit(DoseStatus.Taken, DoseStatus.Pending, isCorrected: false).Should().BeFalse();
    }

    [Fact]
    public void same_status_is_idempotent_for_submit()
        => DoseTransitions.CanSubmit(DoseStatus.Taken, DoseStatus.Taken, isCorrected: false).Should().BeTrue();

    [Fact]
    public void correct_only_writes_missed_or_skipped()
    {
        DoseTransitions.CanCorrect(DoseStatus.Taken, DoseStatus.Missed, isCorrected: false).Should().BeTrue();
        DoseTransitions.CanCorrect(DoseStatus.Taken, DoseStatus.Skipped, isCorrected: false).Should().BeTrue();
        DoseTransitions.CanCorrect(DoseStatus.Pending, DoseStatus.Taken, isCorrected: false).Should().BeFalse();
    }

    [Fact]
    public void rescue_missed_to_taken_goes_through_submit_not_correct()
    {
        // 契约 §7:140/149：correct 的 to_status 只有 missed/skipped；回 taken 走 submit
        DoseTransitions.CanCorrect(DoseStatus.Missed, DoseStatus.Taken, isCorrected: false).Should().BeFalse();
        DoseTransitions.CanSubmit(DoseStatus.Missed, DoseStatus.Taken, isCorrected: false).Should().BeTrue();
    }

    [Fact]
    public void correct_target_whitelist_excludes_taken_and_pending()
    {
        DoseTransitions.IsCorrectTarget(DoseStatus.Missed).Should().BeTrue();
        DoseTransitions.IsCorrectTarget(DoseStatus.Skipped).Should().BeTrue();
        DoseTransitions.IsCorrectTarget(DoseStatus.Taken).Should().BeFalse();
        DoseTransitions.IsCorrectTarget(DoseStatus.Pending).Should().BeFalse();
    }

    [Fact]
    public void correct_is_blocked_once_already_corrected()
        => DoseTransitions.CanCorrect(DoseStatus.Taken, DoseStatus.Missed, isCorrected: true).Should().BeFalse();

    [Theory]
    [InlineData("pending", DoseStatus.Pending)]
    [InlineData("taken", DoseStatus.Taken)]
    [InlineData("missed", DoseStatus.Missed)]
    [InlineData("skipped", DoseStatus.Skipped)]
    public void wire_values_roundtrip(string wire, DoseStatus expected)
    {
        DoseStatusWire.TryParse(wire, out var parsed).Should().BeTrue();
        parsed.Should().Be(expected);
        parsed.ToWire().Should().Be(wire);
    }

    [Fact]
    public void late_is_not_a_status()
        => DoseStatusWire.TryParse("late", out _).Should().BeFalse();
}
