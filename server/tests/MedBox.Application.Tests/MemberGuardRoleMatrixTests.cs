using FluentAssertions;
using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;
using MedBox.Application.Tests.TestSupport;
using NSubstitute;

namespace MedBox.Application.Tests;

/// <summary>
/// <see cref="MemberGuard.AssertMemberAsync"/> 三角色矩阵 + mode 派生口径。
///
/// 两条不能混的轴（AGENTS.md §7.6：mode 管渲染、role 管权限）：
///   role  = admin | member | elder，**入库**，只决定权限；
///   mode  = elder | guardian，**不落库**，由"我在这家里有没有自绑服药人"（selfUids）现算。
/// 所以一位 role=member 的人完全可以是 mode=elder（他确实在吃药，但不能管别人的药）。
/// </summary>
public sealed class MemberGuardRoleMatrixTests
{
    private const string Fid = "F00000000001";
    private const string Openid = "openid_under_test";
    private const long Now = 1_761_955_200_000L;

    private static (IAppDbContext Db, MemberGuard Guard) Build(
        FamilyMember? member,
        params Patient[] patients)
    {
        var db = FakeAppDbContext.Create(
            members: member is null ? [] : [member],
            patients: patients);
        var clock = Substitute.For<IClock>();
        clock.NowMs.Returns(Now);
        return (db, new MemberGuard(db, clock));
    }

    private static FamilyMember MemberOf(string role) => new()
    {
        FamilyId = Fid,
        Openid = Openid,
        Role = role,
        IsPatient = false,
        JoinedAt = Now,
    };

    private static Patient SelfPatient(string pid) => new()
    {
        PatientId = pid,
        FamilyId = Fid,
        Name = "我妈",
        Status = "active",
        SelfOpenid = Openid,
        CreatedAt = Now,
    };

    private static Patient OthersPatient(string pid) => new()
    {
        PatientId = pid,
        FamilyId = Fid,
        Name = "我爸",
        Status = "active",
        SelfOpenid = "someone_else",
        CreatedAt = Now,
    };

    // ── 放行：允许集合包含当前角色 ──────────────────────────────

    [Theory]
    [InlineData("admin", new[] { "admin", "member" })]
    [InlineData("member", new[] { "admin", "member" })]
    [InlineData("elder", new[] { "elder" })]
    [InlineData("admin", new[] { "admin" })]
    public async Task 允许集合包含当前角色时放行(string role, string[] allowed)
    {
        var (_, guard) = Build(MemberOf(role));

        var m = await guard.AssertMemberAsync(Fid, Openid, ct: default, allowed);

        m.Role.Should().Be(role);
        m.FamilyId.Should().Be(Fid);
    }

    /// <summary>allowedRoles 传空数组 = 只要求"是成员"，任何角色都放行。</summary>
    [Theory]
    [InlineData("admin")]
    [InlineData("member")]
    [InlineData("elder")]
    public async Task 不限定角色时三种角色都放行(string role)
    {
        var (_, guard) = Build(MemberOf(role));

        var m = await guard.AssertMemberAsync(Fid, Openid);

        m.Role.Should().Be(role);
    }

    // ── 拒绝：允许集合不包含当前角色 → 1003 ────────────────────

    [Theory]
    [InlineData("member", new[] { "admin" })]
    [InlineData("elder", new[] { "admin", "member" })]
    [InlineData("admin", new[] { "member" })]
    public async Task 允许集合不包含当前角色时拒绝(string role, string[] allowed)
    {
        var (_, guard) = Build(MemberOf(role));

        var act = async () => await guard.AssertMemberAsync(Fid, Openid, default, allowed);

        (await act.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be(1003);
    }

    // ── 拒绝：不是成员 → 1002 ─────────────────────────────────

    [Fact]
    public async Task 非成员抛NotMember且错误码是1002()
    {
        var (_, guard) = Build(null);

        var act = async () => await guard.AssertMemberAsync(Fid, Openid, default, "admin", "member");

        var ex = (await act.Should().ThrowAsync<NotMemberException>()).Which;
        ex.Code.Should().Be(1002);
    }

    // ── 拒绝：fid 为空 → 1005（连库都不查）─────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task fid为空直接参数错误(string? fid)
    {
        var (_, guard) = Build(MemberOf("admin"));

        var act = async () => await guard.AssertMemberAsync(fid!, Openid, default, "admin");

        var ex = (await act.Should().ThrowAsync<ValidationException>()).Which;
        ex.Code.Should().Be(1005);
    }

    // ── mode 由 selfUids 派生，与 role 无关 ─────────────────────

    /// <summary>
    /// 回归护栏：member 自绑了服药人 → mode=elder，但 role 仍是 member。
    /// 老人端主题看 mode，权限看 role；把 mode 从 role 推会同时搞坏这两件事。
    /// </summary>
    [Fact]
    public async Task member自绑服药人时mode是elder但role不变()
    {
        var (_, guard) = Build(MemberOf("member"), SelfPatient("P0000000001"));

        var m = await guard.AssertMemberAsync(Fid, Openid);

        m.Role.Should().Be("member");
        m.Mode.Should().Be("elder");
        m.IsElder.Should().BeTrue();
        m.SelfUids.Should().Equal("P0000000001");
    }

    /// <summary>role=elder 但没自绑任何人 → mode=guardian。role 不推导 mode。</summary>
    [Fact]
    public async Task role是elder但没有自绑时mode仍是guardian()
    {
        var (_, guard) = Build(MemberOf("elder"), OthersPatient("P0000000002"));

        var m = await guard.AssertMemberAsync(Fid, Openid);

        m.Role.Should().Be("elder");
        m.Mode.Should().Be("guardian");
        m.IsElder.Should().BeFalse();
        m.SelfUids.Should().BeEmpty();
    }

    /// <summary>selfUids 只收 status='active' 的自绑服药人；归档的不算（否则退出后还看老人端）。</summary>
    [Fact]
    public async Task 自绑服药人已归档时不算自绑()
    {
        var archived = SelfPatient("P0000000003");
        archived.Status = "archived";
        var (_, guard) = Build(MemberOf("member"), archived);

        var m = await guard.AssertMemberAsync(Fid, Openid);

        m.SelfUids.Should().BeEmpty();
        m.Mode.Should().Be("guardian");
    }

    /// <summary>selfUids 必须是数组：同一人在一个家庭内可绑多位服药人（本人 + 代管）。</summary>
    [Fact]
    public async Task 同一家庭内可绑多位服药人()
    {
        var (_, guard) = Build(
            MemberOf("member"),
            SelfPatient("P0000000001"),
            SelfPatient("P0000000004"),
            OthersPatient("P0000000002"));

        var m = await guard.AssertMemberAsync(Fid, Openid);

        m.SelfUids.Should().Equal("P0000000001", "P0000000004");
    }

    /// <summary>PatientIds 是"这家所有人的服药人"，不含别人家的，也不含归档的。</summary>
    [Fact]
    public async Task PatientIds只含本家庭的active服药人()
    {
        var archived = OthersPatient("P0000000009");
        archived.Status = "archived";
        var foreign = OthersPatient("P0000000008");
        foreign.FamilyId = "F_another";

        var (_, guard) = Build(MemberOf("admin"), SelfPatient("P0000000001"), OthersPatient("P0000000002"), archived, foreign);

        var m = await guard.AssertMemberAsync(Fid, Openid);

        m.PatientIds.Should().Equal("P0000000001", "P0000000002");
    }
}
