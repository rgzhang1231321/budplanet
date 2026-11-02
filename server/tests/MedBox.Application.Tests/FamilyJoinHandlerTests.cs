using FluentAssertions;
using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Application.Tests.TestSupport;
using MedBox.Application.UseCases;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
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
        return (db, new FamilyJoinHandler(db, clock, NullLogger<FamilyJoinHandler>.Instance), info);
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

    /// <summary>
    /// ★ 受邀老人第一次 join（roleIntent='patient'）必须在这一刻绑上自己的档案。
    /// 旧实现的新成员分支只写 family_members、从不碰 patients.self_openid，于是他这一趟
    /// 是「role=elder + selfUids 空」—— 服务端按 role 过滤时手上没有 pid 可用，
    /// 结果就是全家人的药名都下发到他手机上（旧断言甚至把这个缺口写成了"当前口径"）。
    ///
    /// 2026-11-02 晚补：只"新建一条我"仍然不够 —— 子女通常**先建「爸爸」档案再加药**，
    /// 新建出来的空「我」上什么药都没有，爸爸登录后看到的是空药单，家里还多一条重复档案。
    /// 所以首次加入改成"认领邀请人点名的那条"（uid 来自邀请链接，可信）；见下面两条用例。
    /// </summary>
    [Fact]
    public async Task 新成员首次加入时认领邀请人点名的那条档案()
    {
        var family = FamilyWithToken(Token);
        var dad = UnboundPatient();               // 子女先建好的「爸爸」档案：有主名字、还没人绑
        var (db, handler, info) = Build(family, null, user: null, dad);

        // 邀请链接带给爸爸的 uid（/pages/launch?…&uid=P0000000001）
        var dto = await handler.HandleAsync(Fid, Token, "patient", dad.PatientId, Openid, info);

        // ① 成员行：受邀进来就是 elder —— 这是"新成员初始角色"，不是降级
        db.Received(1).Add(Arg.Is<FamilyMember>(m =>
            m.FamilyId == Fid && m.Openid == Openid && m.Role == "elder" && m.IsPatient));
        // ② **不新建**第二条档案：爸爸那条就是他本人，药还挂在上面，认领即可
        db.DidNotReceive().Add(Arg.Is<Patient>(p => p.Name == "我"));
        // ③ 认领落地：这条档案的 self_openid 就是他
        dad.SelfOpenid.Should().Be(Openid);
        dto.SelfUids.Should().ContainSingle().Which.Should().Be(dad.PatientId);
        dto.SelfUid.Should().Be(dad.PatientId);
        dto.Mode.Should().Be("elder");
        dto.Role.Should().Be("elder");
    }

    /// <summary>
    /// 认领的闸门：uid 指向的那条已经被别人绑走了（邀请人发错档案 / 妈妈的码发给了爸爸）。
    /// 此时**绝不抢**，给他新建一条自己的；别人的绑定一个字不动。
    /// </summary>
    [Fact]
    public async Task 新成员点名的那条已被别人绑走时不抢只新建()
    {
        var family = FamilyWithToken(Token);
        var mom = UnboundPatient();
        mom.SelfOpenid = "openid_mom";            // 已经被妈妈绑走
        var (db, handler, info) = Build(family, null, user: null, mom);

        var dto = await handler.HandleAsync(Fid, Token, "patient", mom.PatientId, Openid, info);

        mom.SelfOpenid.Should().Be("openid_mom");                       // 别人的绑定不动
        db.Received(1).Add(Arg.Is<Patient>(p => p.Name == "我" && p.SelfOpenid == Openid));
        dto.SelfUid.Should().NotBe(mom.PatientId);
        dto.SelfUids.Should().HaveCount(1);
    }

    // ── ⑥ 决策 6 的回归护栏：老成员再 join 不改角色，只绑 self ──

    /// <summary>
    /// 本文件最重要的一条。前端 bindSelf 传 roleIntent='patient'，旧实现在这里把 member 降成 elder，
    /// 用户丢的是"能管别人的药"的权限，而且界面上看不出来（他仍看老人端主题）。
    /// 现在的口径：加入家庭与改角色是两个 action，这里只做 self 绑定。
    /// </summary>
    [Fact]
    public async Task 已是member的人再join带patient意图角色不变但绑上自己新建的档案()
    {
        var family = FamilyWithToken(Token);
        var member = MemberWithRole("member");
        var patient = UnboundPatient();
        var (db, handler, info) = Build(family, member, user: null, patient);

        // 注意：这里**故意传了别人的 pid**（旧前端就是这么干的），验证它被忽略
        var dto = await handler.HandleAsync(Fid, Token, "patient", PatientId, Openid, info);

        // 角色一律不动（要改角色只能走 family.update_role）
        member.Role.Should().Be("member");
        member.IsPatient.Should().BeFalse();
        db.DidNotReceive().Add(Arg.Any<FamilyMember>());
        family.MemberCount.Should().Be(1);

        // 自绑只绑到自己新建的「我」上：传进来那条（别人的）一个字不动
        patient.SelfOpenid.Should().BeNull();
        db.Received(1).Add(Arg.Is<Patient>(p => p.Name == "我" && p.SelfOpenid == Openid));
        dto.SelfUid.Should().NotBe(PatientId);
        member.SelfPatientId.Should().Be(dto.SelfUid);

        // mode 由 selfUids 派生：绑上之后他就是老人端主题，但 role 仍是 member
        dto.Role.Should().Be("member");
        dto.Mode.Should().Be("elder");
        dto.SelfUids.Should().HaveCount(1);
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

    /// <summary>
    /// **发版窗口期护栏（旧前端 + 新后端）**：旧版小程序传的是 `uid = patients[0].uid`（=爸爸），
    /// 而爸爸此时 self_openid 恰好是空的 —— 若后端"uid 有值就绑这一位"，爸爸会再次被绑走，
    /// 客户今天的事故就原样复发。现在这条分支**结构上不读 uid**：必须新建「我」，
    /// 且爸爸（真实 pid `Pd791ab565b`，2026-11-02 事故那条）的 self_openid 前后一字不变。
    /// </summary>
    [Fact]
    public async Task 旧前端传爸爸的uid时也只会新建我自己的档案且不碰爸爸()
    {
        var family = FamilyWithToken(Token);
        var member = MemberWithRole("admin");
        // 用客户事故里的真实 pid，避免"换了个假 pid 就测不出问题"的假绿
        var dad = UnboundPatient();
        dad.PatientId = "Pd791ab565b";                    // 爸爸：active、无自绑 = 事故现场
        var before = dad.SelfOpenid;                      // 记下改前值（null）
        var (db, handler, info) = Build(family, member, user: null, dad);

        var dto = await handler.HandleAsync(Fid, Token, "patient", "Pd791ab565b", Openid, info);

        db.Received(1).Add(Arg.Is<Patient>(p =>
            p.Name == "我" && p.Status == "active" && p.SelfOpenid == Openid));
        dad.SelfOpenid.Should().Be(before);               // 前后一字不变（不是"碰巧还是 null"）
        dad.SelfOpenid.Should().NotBe(Openid);            // 没有被绑成本人
        dto.SelfUid.Should().NotBe("Pd791ab565b");        // 绑的不是爸爸
        member.SelfPatientId.Should().Be(dto.SelfUid);
        dto.SelfUids.Should().HaveCount(1);
    }

    /// <summary>同上场景，但"我"的档案已经存在 → 复用，不重复建，爸爸依然不动。</summary>
    [Fact]
    public async Task 旧前端传爸爸的uid时若已有我的档案则复用且不碰爸爸()
    {
        var family = FamilyWithToken(Token);
        var member = MemberWithRole("admin");
        var dad = UnboundPatient();
        var mine = new Patient
        {
            PatientId = "P_mine_0001",
            FamilyId = Fid,
            Name = "我",
            Alias = "我",
            Status = "active",
            SelfOpenid = Openid,
            CreatedAt = Now + 5000,
        };
        var (db, handler, info) = Build(family, member, user: null, dad, mine);

        var dto = await handler.HandleAsync(Fid, Token, "patient", dad.PatientId, Openid, info);

        db.DidNotReceive().Add(Arg.Any<Patient>());
        dad.SelfOpenid.Should().BeNull();
        mine.SelfOpenid.Should().Be(Openid);
        dto.SelfUid.Should().Be("P_mine_0001");
        member.SelfPatientId.Should().Be("P_mine_0001");
    }

    /// <summary>
    /// 别人已经绑着的患者，别人不会被挤掉；本调用方自己另建一条 —— 而且**不再报错**。
    /// 旧口径是"uid 被占 → 1005"，但 uid 已被忽略，这条错误路径随之消失；
    /// 保留这条用例是为了钉住"绝不会把别人的 self_openid 改掉"。
    /// </summary>
    [Fact]
    public async Task 患者已被别人绑时不覆盖别人的绑定()
    {
        var family = FamilyWithToken(Token);
        var member = MemberWithRole("member");
        var patient = UnboundPatient();
        patient.SelfOpenid = "openid_somebody_else";
        var (db, handler, info) = Build(family, member, user: null, patient);

        var dto = await handler.HandleAsync(Fid, Token, "patient", patient.PatientId, Openid, info);

        patient.SelfOpenid.Should().Be("openid_somebody_else");
        db.Received(1).Add(Arg.Is<Patient>(p => p.Name == "我" && p.SelfOpenid == Openid));
        member.SelfPatientId.Should().NotBe(patient.PatientId);
        dto.SelfUid.Should().NotBe(patient.PatientId);
    }

    /// <summary>uid 指向一个根本不存在的 pid：因为 uid 被忽略，照常绑自己的档案，不报错。</summary>
    [Fact]
    public async Task uid指向不存在的服药人也不报错仍绑自己的档案()
    {
        var family = FamilyWithToken(Token);
        var member = MemberWithRole("member");
        var (db, handler, info) = Build(family, member, user: null, UnboundPatient());

        var dto = await handler.HandleAsync(Fid, Token, "patient", "P_not_exist", Openid, info);

        db.Received(1).Add(Arg.Is<Patient>(p => p.Name == "我" && p.SelfOpenid == Openid));
        dto.SelfUids.Should().HaveCount(1);
        dto.Mode.Should().Be("elder");
    }

    // ── ⑦ 2026-11-02 客户事故的回归护栏：绝不把别人绑成"我"─────────────────
    //
    // 事故现象：客户在周报/历史里看到的是爸爸的药，标题写着「我的记录」。
    // 根因链：前端 bindSelf 把 patients[0].uid（=爸爸）传上来 → 后端 uid 命中就直接绑；
    //        而 uid 没有时，老实现还有 `?? 最早创建的 active 患者` 兜底，一样绑到爸爸。
    // 下面三条把"新口径"钉死：uid 为空时必须自己造一条「我」，绝不抓现成的别人。

    /// <summary>
    /// uid 为空 + 家里已有 4 位患者（客户事故里的真实形态）→ 必须**新建一条「我」**并绑，
    /// 别人的 self_openid 一个字不动；**再点一次是幂等的**，不会建出第二条「我」。
    /// 这条是客户本次事故（爸爸被绑成"我"）的直接回归护栏。
    /// </summary>
    [Fact]
    public async Task 带patient意图不给uid时新建我自己的档案而不是绑到别人且重复点不重复建()
    {
        var family = FamilyWithToken(Token);
        var member = MemberWithRole("admin");
        var archivedDad = new Patient           // 客户家里真实存在的第 4 条：已归档的爸爸
        {
            PatientId = "P0000000000",
            FamilyId = Fid,
            Name = "张建国",
            Alias = "爸爸",
            Status = "archived",
            CreatedAt = Now - 2000,
        };
        var dad = UnboundPatient();             // 爸爸：active、无自绑（事故里就是这条被绑走）
        var wife = new Patient
        {
            PatientId = "P0000000002",
            FamilyId = Fid,
            Name = "何冬瑞",
            Alias = "媳妇",
            Status = "active",
            CreatedAt = Now + 1000,
        };
        var mom = new Patient
        {
            PatientId = "P0000000003",
            FamilyId = Fid,
            Name = "看见了",
            Alias = "妈妈",
            Status = "active",
            CreatedAt = Now + 2000,
        };
        var (db, handler, info) = Build(family, member, user: null, archivedDad, dad, wife, mom);

        // ── 第一次点「我自己吃药」──
        var dto = await handler.HandleAsync(Fid, Token, "patient", null, Openid, info);

        db.Received(1).Add(Arg.Is<Patient>(p =>
            p.FamilyId == Fid &&
            p.Name == "我" &&
            p.Status == "active" &&
            p.DisplayMode == "normal" &&
            p.SelfOpenid == Openid));
        // 别人一个都不许被绑走
        dad.SelfOpenid.Should().BeNull();
        wife.SelfOpenid.Should().BeNull();
        mom.SelfOpenid.Should().BeNull();
        archivedDad.SelfOpenid.Should().BeNull();
        // 绑给的必须是新建那条，不是四位里任何一位
        var mine = dto.SelfUid;
        mine.Should().NotBeNullOrEmpty();
        new[] { archivedDad.PatientId, dad.PatientId, wife.PatientId, mom.PatientId }
            .Should().NotContain(mine!);
        dto.SelfUids.Should().HaveCount(1);
        dto.Mode.Should().Be("elder");
        member.SelfPatientId.Should().Be(mine);

        // ── 第二次点（下拉重进 / 重复点击）→ 幂等，仍然只有一条「我」──
        var dto2 = await handler.HandleAsync(Fid, Token, "patient", null, Openid, info);

        db.Received(1).Add(Arg.Any<Patient>());      // 总共只建过一条
        dto2.SelfUid.Should().Be(mine);
        dto2.SelfUids.Should().HaveCount(1);
        dad.SelfOpenid.Should().BeNull();
    }

    /// <summary>uid 为空但"已经绑给我了" → 幂等复用，不重复建第二条「我」。</summary>
    [Fact]
    public async Task 带patient意图不给uid时已有本人档案则复用不重复建()
    {
        var family = FamilyWithToken(Token);
        var member = MemberWithRole("admin");
        var mine = new Patient
        {
            PatientId = "P_mine_0001",
            FamilyId = Fid,
            Name = "我",
            Alias = "我",
            Status = "active",
            SelfOpenid = Openid,
            CreatedAt = Now,
        };
        var (db, handler, info) = Build(family, member, user: null, mine, UnboundPatient());

        var dto = await handler.HandleAsync(Fid, Token, "patient", null, Openid, info);

        db.DidNotReceive().Add(Arg.Any<Patient>());
        mine.SelfOpenid.Should().Be(Openid);
        member.SelfPatientId.Should().Be("P_mine_0001");
        dto.SelfUid.Should().Be("P_mine_0001");
    }

    // （原「uid 指向不存在的服药人 → 1005」用例已删：uid 在这条分支被忽略，
    //   该错误路径不存在了；等价护栏见「uid指向不存在的服药人也不报错仍绑自己的档案」。）

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
