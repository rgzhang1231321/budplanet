using FluentAssertions;
using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Domain.Entities;
using NSubstitute;

namespace MedBox.Domain.Tests;

/// <summary>
/// 越权必须落审计（AGENTS §6.2）。用例自己判出来的越权（elder 只能记/延后自己的药）
/// 走 <see cref="IMemberGuard.AuditForbiddenAsync"/>：result_code 固定 1003，action 指向具体用例。
/// </summary>
public class MemberGuardAuditTests
{
    [Fact]
    public async Task forbidden_audit_is_written_as_1003()
    {
        var db = Substitute.For<IAppDbContext>();
        var clock = Substitute.For<IClock>();
        clock.NowMs.Returns(1_761_955_200_000L);
        var guard = new MemberGuard(db, clock);

        await guard.AuditForbiddenAsync("f1", "openid_1", "checkin.snooze");

        db.Received(1).Add(Arg.Is<AuditLog>(a =>
            a.ResultCode == 1003 &&
            a.Action == "checkin.snooze" &&
            a.FamilyId == "f1" &&
            a.Openid == "openid_1"));
        await db.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
