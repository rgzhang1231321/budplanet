using FluentAssertions;
using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Application.Tests.TestSupport;
using MedBox.Application.UseCases;
using MedBox.Domain;
using MedBox.Domain.Entities;
using NSubstitute;

namespace MedBox.Application.Tests;

/// <summary>
/// family.join 的成员判定与角色口径（审计 §13.4 点名的第 1 项）。
///
/// 历史上这里有个真 bug：<c>roleIntent=='patient'</c> 时把非 admin 成员静默降级成 elder，
/// 而前端 bindSelf 正好传 'patient'，于是"加入家庭"把一位 member 的权限打掉了。
/// 2026-11-02 已按决策 6 删掉降级分支（commit 5638974），本文件就是那条回归护栏：
/// 加入家庭与改角色是两个语义，老成员再 join 角色一律不动，但 self 绑定仍然生效。
/// </summary>
public sealed class FamilyJoinHandlerTests
{
    private const string Fid = "F00000000001";
    private const string Token = "IVT1A2B3C4D";
    private const string Openid = "openid_joiner";
    private const string PatientId = "P0000000001";
    private const long Now = 1_761_955_200_000L;

    private static IClock Clock()
    {
        var clock = Substitute.For<IClock>();
        clock.NowMs.Returns(Now);
        return clock;
    }

    /// <summary>
    /// 拼一整套 family.join 需要的东西。注意 <see cref="FamilyJoinHandler"/> 结尾还要再调一次
    /// <see cref="FamilyInfoHandler"/>（内部重新查成员 + 自绑服药人），所以替身必须把 Add 落到真实 List 上，
    /// 否则新成员场景会以 NotMemberException 收场。
    /// </summary>
    private static (IAppDbContext Db, FamilyJoinHandler Handler, FamilyInfoHandler Info) Build(
        Family family,
        FamilyMember? member,
        User? user = null,
        params Patient[] patients)
    {
        user ??= new User
        {
            Openid = Openid,
            FamilyIds = ["F_other_family"],
            CreatedAt = Now,
        };
        var db = FakeAppDbContext.Create(
            families: [family],
            members: member is null ? [] : [member],
            patients: patients,
            users: [user]);
        var clock = Clock();
        var guard = new MemberGuard(db, clock);
        var info = new FamilyInfoHandler(db, guard, clock);
        return (db, new FamilyJoinHandler(db, clock), info);
    }

    private static Family FamilyWithToken(string? token) => new()
    {
        FamilyId = Fid,
        Name = "我家",
        OwnerOpenid = "openid_owner",
        InviteToken = token,
        Tier = "free",
        MemberCount = 1,
        CreatedAt = Now,
    };

    private static FamilyMember MemberWithRole(string role) => new()
    {
        FamilyId = Fid,
        Openid = Openid,
        Role = role,
        IsPatient = false,
        SelfPatientId = null,
        JoinedAt = Now,
    };

    private static Patient UnboundPatient() => new()
    {
        PatientId = PatientId,
        FamilyId = Fid,
        Name = "我妈",
        Status = "active",
        SelfOpenid = null,
        CreatedAt = Now,
    };

    // ── ① 成员短路验码：重复 join 是幂等调用，不抛 ──────────────

    /// <summary>
    /// 前端 bindSelf 会带着 fid 再 join 一次，而非 admin 手里 invite_token 是空串（只给 admin 下发）。
    /// 先验码后查成员的旧顺序会把这次幂等调用顶成 1005。所以必须是"先判成员，是成员就跳过验码"。
    /// </summary>
    [Theory]
    [InlineData("")]              // 前端手里根本没有码
    [InlineData("IVTWRONG12")]    // 码过期了 / 输错了
    [InlineData(null)]
    public async Task 已是成员时带任何邀请码都能重复加入不抛异常(string? ivt)
    {
        var family = FamilyWithToken(Token);
        var (db, handler, info) = Build(family, MemberWithRole("member"), user: null, UnboundPatient());

        var dto = await handler.HandleAsync(Fid, ivt!, "guardian", null, Openid, info);

        dto.Fid.Should().Be(Fid);
        dto.Role.Should().Be("member");
        // 幂等调用不该动任何成员行
        db.DidNotReceive().Add(Arg.Any<FamilyMember>());
        family.MemberCount.Should().Be(1);
    }

    // ── ②③ 非成员：空令牌 / 错码都拒绝 ───────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("IVTWRONG12")]
    [InlineData("ivt1a2b3c4d")]   // 逐字比较，大小写不同就是不对
    public async Task 非成员带不对的邀请码拒绝(string ivt)
    {
        var family = FamilyWithToken(Token);
        var (_, handler, info) = Build(family, null, user: null, UnboundPatient());

        var act = async () => await handler.HandleAsync(Fid, ivt, "guardian", null, Openid, info);

        var ex = (await act.Should().ThrowAsync<ValidationException>()).Which;
        ex.Code.Should().Be(1005);
    }

    /// <summary>家庭没有令牌（NULL/空串）时新成员一律拒绝 —— 没码可验 = 任何人凭 fid 都能进。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task 家庭没有令牌时非成员一律拒绝(string? familyToken)
    {
        var family = FamilyWithToken(familyToken);
        var (_, handler, info) = Build(family, null, user: null, UnboundPatient());

        var act = async () => await handler.HandleAsync(Fid, "IVTANYTHING", "guardian", null, Openid, info);

        (await act.Should().ThrowAsync<ValidationException>()).Which.Code.Should().Be(1005);
    }

    // ── ④⑤ 新成员：对码加入，roleIntent 决定初始角色 ──────────

    [Fact]
    public async Task 非成员带对的邀请码加入家庭并成为member()
    {
        var family = FamilyWithToken(Token);
        var (db, handler, info) = Build(family, null, user: null, UnboundPatient());

        var dto = await handler.HandleAsync(Fid, Token, "guardian", null, Openid, info);

        db.Received(1).Add(Arg.Is<FamilyMember>(m =>
            m.FamilyId == Fid &&
            m.Openid == Openid &&
            m.Role == "member" &&
            m.IsPatient == false));
        family.MemberCount.Should().Be(2);
        dto.Role.Should().Be("member");
        dto.Mode.Should().Be("guardian");
    }

    /// <summary>受邀进来的老人第一次加入就该是 elder —— 这是"新成员初始角色"，不是降级。</summary>
    [Fact]
    public async Task 新成员roleIntent是patient时初始角色是elder()
    {
        var family = FamilyWithToken(Token);
        var (db, handler, info) = Build(family, null, user: null, UnboundPatient());

        var dto = await handler.HandleAsync(Fid, Token, "patient", PatientId, Openid, info);

        db.Received(1).Add(Arg.Is<FamilyMember>(m =>
            m.Role == "elder" &&
            m.IsPatient == true));
        dto.Role.Should().Be("elder");

        // ⚠ 这里断言的是**当前实现**的口径，不是我认为它该有的样子：
        // self_openid 的绑定只长在"老成员"分支上（else if roleIntent == "patient"），
        // 新成员分支只建 family_members 行，不碰 patients.self_openid。
        // 后果：一个受邀进来的老人第一次加入时 mode 仍是 guardian（看不到老人端主题），
        // 要等他下一次带 uid 再 join 一次才会绑上。已在交付说明里记为待确认缺口，本轮不改生产代码。
        dto.SelfUid.Should().BeNull();
        dto.Mode.Should().Be("guardian");
    }

    // ── ⑥ 决策 6 的回归护栏：老成员再 join 不改角色，只绑 self ──

    /// <summary>
    /// 本文件最重要的一条。前端 bindSelf 传 roleIntent='patient'，旧实现在这里把 member 降成 elder，
    /// 用户丢的是"能管别人的药"的权限，而且界面上看不出来（他仍看老人端主题）。
    /// 现在的口径：加入家庭与改角色是两个 action，这里只做 self 绑定。
    /// </summary>
    [Fact]
    public async Task 已是member的人再join带patient意图角色不变但绑上self()
    {
        var family = FamilyWithToken(Token);
        var member = MemberWithRole("member");
        var patient = UnboundPatient();
        var (db, handler, info) = Build(family, member, user: null, patient);

        var dto = await handler.HandleAsync(Fid, Token, "patient", PatientId, Openid, info);

        // 角色一律不动（要改角色只能走 family.update_role）
        member.Role.Should().Be("member");
        member.IsPatient.Should().BeFalse();
        db.DidNotReceive().Add(Arg.Any<FamilyMember>());
        family.MemberCount.Should().Be(1);

        // 但 bindSelf 真正需要的东西照做：self_openid 与 SelfPatientId 都绑上
        patient.SelfOpenid.Should().Be(Openid);
        member.SelfPatientId.Should().Be(PatientId);

        // mode 由 selfUids 派生：绑上自绑服药人之后他就是老人端，但 role 仍是 member
        dto.Role.Should().Be("member");
        dto.Mode.Should().Be("elder");
        dto.SelfUid.Should().Be(PatientId);
        dto.SelfUids.Should().Equal(PatientId);
    }

    /// <summary>admin 同样不能被 join 顺带降级。</summary>
    [Fact]
    public async Task admin再join带patient意图角色仍是admin()
    {
        var family = FamilyWithToken(Token);
        var member = MemberWithRole("admin");
        var (db, handler, info) = Build(family, member, user: null, UnboundPatient());

        var dto = await handler.HandleAsync(Fid, Token, "patient", PatientId, Openid, info);

        member.Role.Should().Be("admin");
        db.DidNotReceive().Add(Arg.Any<FamilyMember>());
        dto.Role.Should().Be("admin");
    }

    /// <summary>自绑服药人已经被别人绑走时不再重复绑（self_openid 是排他的）。</summary>
    [Fact]
    public async Task 自绑服药人已被别人绑走时不覆盖()
    {
        var family = FamilyWithToken(Token);
        var member = MemberWithRole("member");
        var patient = UnboundPatient();
        patient.SelfOpenid = "openid_somebody_else";
        var (_, handler, info) = Build(family, member, user: null, patient);

        await handler.HandleAsync(Fid, Token, "patient", PatientId, Openid, info);

        patient.SelfOpenid.Should().Be("openid_somebody_else");
        member.SelfPatientId.Should().BeNull();
    }

    /// <summary>fid 为空时按邀请码反查家庭 —— 手动输码的人只知道码，不该知道 fid。</summary>
    [Fact]
    public async Task fid为空时按邀请码反查家庭()
    {
        var family = FamilyWithToken(Token);
        var (db, handler, info) = Build(family, null, user: null, UnboundPatient());

        var dto = await handler.HandleAsync("", Token, "guardian", null, Openid, info);

        dto.Fid.Should().Be(Fid);
        db.Received(1).Add(Arg.Is<FamilyMember>(m => m.FamilyId == Fid && m.Openid == Openid));
    }

    /// <summary>fid 与邀请码都为空 → 参数错误（不是 1009）。</summary>
    [Fact]
    public async Task fid和邀请码都为空时参数错误()
    {
        var family = FamilyWithToken(Token);
        var (_, handler, info) = Build(family, null, user: null, UnboundPatient());

        var act = async () => await handler.HandleAsync("", "", "guardian", null, Openid, info);

        (await act.Should().ThrowAsync<ValidationException>()).Which.Code.Should().Be(1005);
    }

    /// <summary>fid 指向不存在的家庭 → 参数错误。</summary>
    [Fact]
    public async Task 家庭不存在时参数错误()
    {
        var family = FamilyWithToken(Token);
        var (_, handler, info) = Build(family, null, user: null, UnboundPatient());

        var act = async () => await handler.HandleAsync("F_does_not_exist", Token, "guardian", null, Openid, info);

        (await act.Should().ThrowAsync<ValidationException>()).Which.Code.Should().Be(1005);
    }

    /// <summary>加入成功后 users.family_ids 要补上这个家庭（冗余列，登录一次返回）。</summary>
    [Fact]
    public async Task 加入成功后用户的family_ids补上该家庭()
    {
        var family = FamilyWithToken(Token);
        var user = new User { Openid = Openid, FamilyIds = ["F_other_family"], CreatedAt = Now };
        var (db, handler, info) = Build(family, null, user, UnboundPatient());

        await handler.HandleAsync(Fid, Token, "guardian", null, Openid, info);

        user.FamilyIds.Should().Equal("F_other_family", Fid);
        _ = db.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
