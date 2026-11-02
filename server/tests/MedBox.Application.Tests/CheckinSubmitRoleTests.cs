using FluentAssertions;
using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using MedBox.Application.Services;
using MedBox.Application.Tests.TestSupport;
using MedBox.Application.UseCases;
using MedBox.Domain.Entities;
using NSubstitute;

namespace MedBox.Application.Tests;

/// <summary>
/// `checkin.submit` / `checkin.snooze` 的「只能记自己的药」判据必须是 **role**，
/// 不是"我在这家里有没有自绑服药人"（<see cref="Membership.IsElder"/>）。
///
/// 两条轴不能混（契约 §4.1 / AGENTS §7.6：mode 管渲染、role 管权限）：
///   role = admin | member | elder → 权限；
///   mode = elder | guardian      → 渲染，由 selfUids 有没有值现算。
/// 「子女自己也吃降压药、同时管全家」是契约点名的常见组合：这类人 role=admin、
/// 同时又 IsElder=true。用 IsElder 判权限的后果是 schedule.today 把全家的药下发了（按 role），
/// 他到打卡接口又被拦（按 IsElder）→「看得见、点不动」，且没有任何文案能解释。
/// </summary>
public sealed class CheckinSubmitRoleTests
{
    private const string Fid = "F00000000001";
    private const string Openid = "openid_child";
    private const string SelfPid = "P0000000001"; // 绑在我自己名下的那条
    private const string DadPid = "P0000000002"; // 我爸，绑在别人名下
    private const string Date = "2026-11-02";
    private const long Now = 1_761_955_200_000L;

    private static SubmitHandler Build(string role)
    {
        var member = new FamilyMember { FamilyId = Fid, Openid = Openid, Role = role, JoinedAt = Now };
        // 关键：这位"子女"自己名下也有一条服药人档案 → IsElder 恒为 true，但 role 仍是 admin
        var selfPatient = new Patient
        {
            PatientId = SelfPid, FamilyId = Fid, Name = "我", SelfOpenid = Openid, Status = "active", CreatedAt = Now,
        };
        var dad = new Patient
        {
            PatientId = DadPid, FamilyId = Fid, Name = "我爸", SelfOpenid = "openid_dad", Status = "active", CreatedAt = Now - 1000,
        };
        var db = FakeAppDbContext.Create(members: [member], patients: [selfPatient, dad]);

        var clock = Substitute.For<IClock>();
        clock.NowMs.Returns(Now);
        clock.TodayCn.Returns(Date);

        // CAS 正常路径：快照是 pending，写入成功。本测试只验权限闸，别让 CAS 的返回值干扰
        db.GetDoseSnapshotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DoseSnapshot?>(new DoseSnapshot("pending", false, Date, 1)));
        db.CompareAndSetDoseAsync(Arg.Any<DoseCasUpdate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1));

        var guard = new MemberGuard(db, clock);
        var cas = new DoseCasApplier(db, clock, lateToleranceMin: 120);
        return new SubmitHandler(db, guard, clock, cas);
    }

    private static SubmitRequest One(string pid) => new()
    {
        Items = [new SubmitItem { Pid = pid, Date = Date, Slot = 1, Status = "taken" }],
    };

    [Fact]
    public async Task 自己也吃药的子女_可以替家人打卡()
    {
        var res = await Build("admin").HandleAsync(One(DadPid), Openid);

        res.Rejected.Should().NotContain(r => r.Code == 1003, "role=admin 的人有权限代谢家人的记录");
        res.Applied.Should().Contain(a => a.Pid == DadPid);
    }

    [Fact]
    public async Task 普通成员_可以替家人打卡()
    {
        var res = await Build("member").HandleAsync(One(DadPid), Openid);

        res.Rejected.Should().NotContain(r => r.Code == 1003);
    }

    [Fact]
    public async Task 纯服药人_不能替家人打卡()
    {
        var res = await Build("elder").HandleAsync(One(DadPid), Openid);

        res.Rejected.Should().Contain(r => r.Code == 1003 && r.Pid == DadPid);
        res.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task 纯服药人_可以记自己的药()
    {
        var res = await Build("elder").HandleAsync(One(SelfPid), Openid);

        res.Rejected.Should().NotContain(r => r.Code == 1003);
        res.Applied.Should().Contain(a => a.Pid == SelfPid);
    }
}
