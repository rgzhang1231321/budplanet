using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

/// <summary>
/// 打卡 CAS 的重试规则。测的是"什么时候允许打、什么时候必须判 rejected"，
/// 不是某个固定返回值 —— 并发下第一次打不进去必须重读，第二次还打不进去必须拒绝而不是假装成功。
/// </summary>
public class DoseCasPolicyTests
{
    [Fact]
    public void first_attempt_on_pending_is_applied()
        => DoseCasPolicy.Decide(DoseStatus.Pending, DoseStatus.Taken, isCorrected: false, attemptsUsed: 0)
            .Should().Be(CasStep.Apply);

    [Fact]
    public void after_a_lost_race_system_missed_can_still_be_rescued()
        // 漏服判定抢先把自己改成 missed（未人工改判）→ 重试一次仍允许改成 taken
        => DoseCasPolicy.Decide(DoseStatus.Missed, DoseStatus.Taken, isCorrected: false, attemptsUsed: 1)
            .Should().Be(CasStep.Apply);

    [Fact]
    public void manually_corrected_missed_is_rejected()
        => DoseCasPolicy.Decide(DoseStatus.Missed, DoseStatus.Taken, isCorrected: true, attemptsUsed: 0)
            .Should().Be(CasStep.Reject);

    [Fact]
    public void skipped_is_terminal()
        => DoseCasPolicy.Decide(DoseStatus.Skipped, DoseStatus.Taken, isCorrected: false, attemptsUsed: 0)
            .Should().Be(CasStep.Reject);

    [Theory]
    [InlineData(DoseStatus.Taken, DoseStatus.Taken)]
    [InlineData(DoseStatus.Skipped, DoseStatus.Skipped)]
    [InlineData(DoseStatus.Missed, DoseStatus.Missed)]
    public void same_status_is_idempotent_not_an_error(DoseStatus from, DoseStatus to)
        // 已经是要写的状态 → changed=false 的空操作，仍要出队（不是 rejected）
        => DoseCasPolicy.Decide(from, to, isCorrected: false, attemptsUsed: 0).Should().Be(CasStep.AlreadyDone);

    [Fact]
    public void attempts_are_bounded_so_a_hot_row_cannot_spin_forever()
    {
        // 每次都有人抢先改 → 第 MaxAttempts 次起必须拒绝，绝不无限重试
        DoseCasPolicy.Decide(DoseStatus.Pending, DoseStatus.Taken, isCorrected: false, attemptsUsed: DoseCasPolicy.MaxAttempts)
            .Should().Be(CasStep.Reject);
        DoseCasPolicy.MaxAttempts.Should().BeGreaterThan(0);
    }
}
