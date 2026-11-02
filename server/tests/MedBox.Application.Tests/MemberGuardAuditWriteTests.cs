using FluentAssertions;
using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Application.Tests.TestSupport;
using MedBox.Domain;
using MedBox.Domain.Entities;
using NSubstitute;

namespace MedBox.Application.Tests;

/// <summary>
/// 越权必写 audit_logs（AGENTS.md §6.2：越权 1002 / 1003 必须落审计）。
/// 审计是"谁能看出有人试过越权"的唯一线索，漏写一条就意味着一次未授权访问在系统里无声无息。
///
/// 覆盖三个写入点：
///   AssertMemberAsync 判出的 1002（不是成员）  → action = "assert_member"
///   AssertMemberAsync 判出的 1003（角色不符）  → action = "assert_role:&lt;允许集合&gt;"
///   用例自己判出的 1003（elder 只能记自己的药）→ AuditForbiddenAsync，action 指向具体用例
/// </summary>
public sealed class MemberGuardAuditWriteTests
{
    private const string Fid = "F00000000001";
    private const string Openid = "openid_intruder";
    private const long Now = 1_761_955_200_000L;

    private static (IAppDbContext Db, MemberGuard Guard) Build(FamilyMember? member, params Patient[] patients)
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
        JoinedAt = Now,
    };

    private static AuditLog SingleAudit(IAppDbContext db)
        => db.ReceivedCalls()
            .Select(c => c.GetArguments())
            .SelectMany(a => a)
            .OfType<AuditLog>()
            .Single();

    // ── 1002：不是成员 ────────────────────────────────────────

    [Fact]
    public async Task 非成员越权写一条assert_member审计错误码1002()
    {
        var (db, guard) = Build(null);

        await guard.Awaiting(g => g.AssertMemberAsync(Fid, Openid, default, "admin"))
            .Should().ThrowAsync<NotMemberException>();

        var audit = SingleAudit(db);
        audit.Action.Should().Be("assert_member");
        audit.ResultCode.Should().Be(1002);
        audit.FamilyId.Should().Be(Fid);
        audit.Openid.Should().Be(Openid);
        audit.Detail.Should().Be("{}");
        audit.CreatedAt.Should().Be(Now);
        audit.LogId.Should().NotBeNullOrEmpty();
        // 写完必须真的落库（Add 了但不 SaveChanges 等于没写）
        await db.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── 1003：角色不符 ────────────────────────────────────────

    [Fact]
    public async Task 角色不符越权写一条assert_role审计错误码1003()
    {
        var (db, guard) = Build(MemberOf("elder"));

        await guard.Awaiting(g => g.AssertMemberAsync(Fid, Openid, default, "admin", "member"))
            .Should().ThrowAsync<ForbiddenException>();

        var audit = SingleAudit(db);
        audit.Action.Should().Be("assert_role:admin|member");
        audit.ResultCode.Should().Be(1003);
        audit.FamilyId.Should().Be(Fid);
        audit.Openid.Should().Be(Openid);
        audit.CreatedAt.Should().Be(Now);
    }

    /// <summary>action 里带的是**允许集合**，不是"用户当前角色"—— 否则事后看不出他被哪条规则挡住。</summary>
    [Fact]
    public async Task 单角色场景的action只含那一个角色()
    {
        var (db, guard) = Build(MemberOf("member"));

        await guard.Awaiting(g => g.AssertMemberAsync(Fid, Openid, default, "admin"))
            .Should().ThrowAsync<ForbiddenException>();

        SingleAudit(db).Action.Should().Be("assert_role:admin");
    }

    /// <summary>1002 与 1003 是两条不同的审计，不能只写一条。</summary>
    [Fact]
    public async Task 同一次调用只写一条审计()
    {
        var (db, guard) = Build(MemberOf("member"));

        await guard.Awaiting(g => g.AssertMemberAsync(Fid, Openid, default, "admin"))
            .Should().ThrowAsync<ForbiddenException>();

        db.ReceivedCalls()
            .SelectMany(c => c.GetArguments())
            .OfType<AuditLog>()
            .Should().HaveCount(1);
    }

    // ── 用例自己判出的 1003 ───────────────────────────────────

    /// <summary>
    /// elder 只能记/延后自己的药，这条用例自己判（MemberGuard 看不到 item.Pid），
    /// 所以必须由用例显式补落审计。AssertMember 已经放行，不会再写第二次。
    /// </summary>
    [Theory]
    [InlineData("checkin.submit")]
    [InlineData("checkin.snooze")]
    [InlineData("checkin.correct")]
    public async Task 用例自判越权时补落一条1003审计(string action)
    {
        var (db, guard) = Build(MemberOf("elder"));

        await guard.AuditForbiddenAsync(Fid, Openid, action);

        var audit = SingleAudit(db);
        audit.Action.Should().Be(action);
        audit.ResultCode.Should().Be(1003);
        audit.FamilyId.Should().Be(Fid);
        audit.Openid.Should().Be(Openid);
        audit.CreatedAt.Should().Be(Now);
        await db.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>AuditForbiddenAsync 的 result_code 恒为 1003，不接受调用方传别的码。</summary>
    [Fact]
    public async Task AuditForbidden的错误码固定1003()
    {
        var (db, guard) = Build(MemberOf("elder"));

        await guard.AuditForbiddenAsync(Fid, Openid, "whatever");

        SingleAudit(db).ResultCode.Should().Be(1003);
    }

    // ── 不该写审计的场景（写多了比写少了更难查）─────────────────

    [Theory]
    [InlineData("admin")]
    [InlineData("member")]
    [InlineData("elder")]
    public async Task 正常放行时不写任何审计(string role)
    {
        var (db, guard) = Build(MemberOf(role));

        await guard.AssertMemberAsync(Fid, Openid);

        db.ReceivedCalls()
            .SelectMany(c => c.GetArguments())
            .OfType<AuditLog>()
            .Should().BeEmpty();
    }

    [Fact]
    public async Task fid为空时直接参数错误且不写审计()
    {
        var (db, guard) = Build(MemberOf("admin"));

        await guard.Awaiting(g => g.AssertMemberAsync("", Openid, default, "admin"))
            .Should().ThrowAsync<ValidationException>();

        db.ReceivedCalls()
            .SelectMany(c => c.GetArguments())
            .OfType<AuditLog>()
            .Should().BeEmpty();
    }
}
