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
/// v1.9 的两个管理员入口：**给家人起称呼** 与 **手动绑定服药档案**。
///
/// 起因是客户一句：「邀请码的话进来后没法知道是谁，拿管理员能手动修改或者绑定这个家庭人员是谁吗？」
/// —— 小程序端早已拿不到微信昵称，family.info 里除自己以外所有成员的 nick 都是"家人"，
/// 邀请三四个人后列表上是四个"家人"，既分不清谁是谁，也没法判断该给谁收回权限。
///
/// 绑定那部分同时是 2026-11-02 事故的人工救济口：那次「爸爸的 self_openid 被错绑成子女账号」
/// 只能改数据库，本文件钉住"管理员自己就能改回来"，以及**不许抢别人已生效的绑定**。
/// </summary>
public sealed class FamilyMemberAdminTests
{
    private const string Fid = "F00000000001";
    private const string AdminOpenid = "openid_admin";
    private const string SisterOpenid = "openid_sister";
    private const string MomOpenid = "openid_mom";
    private const string DadPid = "P0000000001";
    private const string MomPid = "P0000000002";
    private const long Now = 1_761_955_200_000L;

    private static IClock Clock()
    {
        var clock = Substitute.For<IClock>();
        clock.NowMs.Returns(Now);
        return clock;
    }

    private static FamilyMember Member(string openid, string role, string? relation = null) => new()
    {
        FamilyId = Fid,
        Openid = openid,
        Role = role,
        Relation = relation,
        JoinedAt = Now,
    };

    private static Patient PatientOf(string pid, string alias, string? selfOpenid = null) => new()
    {
        PatientId = pid,
        FamilyId = Fid,
        Name = alias,
        Alias = alias,
        Status = "active",
        SelfOpenid = selfOpenid,
        CreatedAt = Now,
    };

    private static ContentSecurityChecker Security()
    {
        var wx = Substitute.For<IWxApiClient>();
        wx.MsgSecCheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WxSecVerdict.Pass);
        return new ContentSecurityChecker(wx);
    }

    private sealed record Kit(IAppDbContext Db, FamilyInfoHandler Info, FamilyMember Admin, FamilyMember Sister);

    private static Kit Build(params Patient[] patients)
    {
        var admin = Member(AdminOpenid, "admin", "我");
        var sister = Member(SisterOpenid, "member");
        var db = FakeAppDbContext.Create(
            families:
            [
                new Family
                {
                    FamilyId = Fid, Name = "我家", OwnerOpenid = AdminOpenid,
                    InviteToken = "IVT1A2B3C4D", Tier = "free", MemberCount = 2, CreatedAt = Now,
                },
            ],
            members: [admin, sister],
            patients: patients);
        var clock = Clock();
        return new Kit(db, new FamilyInfoHandler(db, new MemberGuard(db, clock), clock), admin, sister);
    }

    // ───────────────────────── 改称呼 ─────────────────────────

    /// <summary>管理员给成员起称呼 → 落在该成员行上，family.info 能把 label 读回来。</summary>
    [Fact]
    public async Task 管理员可以给家人起称呼且能读回来()
    {
        var kit = Build();
        var handler = new FamilySetMemberNameHandler(kit.Db, new MemberGuard(kit.Db, Clock()), Security(), Clock());

        await handler.HandleAsync(Fid, AdminOpenid, SisterOpenid, "姐姐");

        kit.Sister.Relation.Should().Be("姐姐");
        var info = await kit.Info.HandleAsync(Fid, AdminOpenid);
        info.Members.Single(m => m.Openid == SisterOpenid).Label.Should().Be("姐姐");
    }

    /// <summary>传空串 = 清掉自定义称呼（前端回落微信昵称），不是"把称呼改成空字符串"。</summary>
    [Fact]
    public async Task 称呼传空串等于清掉自定义称呼()
    {
        var kit = Build();
        kit.Sister.Relation = "姐姐";
        var handler = new FamilySetMemberNameHandler(kit.Db, new MemberGuard(kit.Db, Clock()), Security(), Clock());

        await handler.HandleAsync(Fid, AdminOpenid, SisterOpenid, "   ");

        kit.Sister.Relation.Should().BeNull();
        var info = await kit.Info.HandleAsync(Fid, AdminOpenid);
        info.Members.Single(m => m.Openid == SisterOpenid).Label.Should().BeNull();
    }

    /// <summary>称呼会显示给全家人，长度要在服务端卡死 —— 只靠前端 maxlength 等于没卡。</summary>
    [Fact]
    public async Task 称呼超长被拒()
    {
        var kit = Build();
        var handler = new FamilySetMemberNameHandler(kit.Db, new MemberGuard(kit.Db, Clock()), Security(), Clock());

        var act = () => handler.HandleAsync(Fid, AdminOpenid, SisterOpenid, new string('爸', 13));

        await act.Should().ThrowAsync<ValidationException>();
        kit.Sister.Relation.Should().BeNull();
    }

    /// <summary>普通成员不能改别人的称呼（否则谁都能给全家改名）。</summary>
    [Fact]
    public async Task 非管理员不能改称呼()
    {
        var kit = Build();
        var handler = new FamilySetMemberNameHandler(kit.Db, new MemberGuard(kit.Db, Clock()), Security(), Clock());

        var act = () => handler.HandleAsync(Fid, SisterOpenid, AdminOpenid, "老板");

        await act.Should().ThrowAsync<ForbiddenException>();
        kit.Admin.Relation.Should().Be("我");
    }

    // ───────────────────────── 绑定服药人 ─────────────────────────

    /// <summary>把「姐姐」绑到爸爸那条档案：两边同时落库（patients.self_openid + family_members.self_patient_id）。</summary>
    [Fact]
    public async Task 管理员可以把成员绑到无主档案()
    {
        var kit = Build(PatientOf(DadPid, "爸爸"));
        var handler = new FamilyBindMemberHandler(kit.Db, new MemberGuard(kit.Db, Clock()), Clock());

        await handler.HandleAsync(Fid, AdminOpenid, SisterOpenid, DadPid);

        var dad = kit.Db.Patients.Single(p => p.PatientId == DadPid);
        dad.SelfOpenid.Should().Be(SisterOpenid);
        kit.Sister.SelfPatientId.Should().Be(DadPid);
    }

    /// <summary>
    /// ★ 核心安全口径：目标档案已绑在别人身上时**必须拒绝**，且提示里要点名"绑给了谁"。
    /// 静默覆盖正是 2026-11-02 事故的成因；只回一句"已被占用"管理员也不知道该去解绑哪位。
    /// </summary>
    [Fact]
    public async Task 已绑给别人的档案不能被直接抢走()
    {
        var kit = Build(PatientOf(MomPid, "妈妈", MomOpenid));
        kit.Db.Add(Member(MomOpenid, "member", "妈妈"));
        var handler = new FamilyBindMemberHandler(kit.Db, new MemberGuard(kit.Db, Clock()), Clock());

        var act = () => handler.HandleAsync(Fid, AdminOpenid, SisterOpenid, MomPid);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().Contain("妈妈");
        kit.Db.Patients.Single(p => p.PatientId == MomPid).SelfOpenid.Should().Be(MomOpenid);
        kit.Sister.SelfPatientId.Should().BeNull();
    }

    /// <summary>换绑时把这位成员**原来那条**一起解掉，否则一个人同时挂两条档案，mode 与老人端过滤会各拿一条。</summary>
    [Fact]
    public async Task 换绑会解除该成员原来的档案()
    {
        var kit = Build(PatientOf(MomPid, "妈妈", SisterOpenid), PatientOf(DadPid, "爸爸"));
        kit.Sister.SelfPatientId = MomPid;
        var handler = new FamilyBindMemberHandler(kit.Db, new MemberGuard(kit.Db, Clock()), Clock());

        await handler.HandleAsync(Fid, AdminOpenid, SisterOpenid, DadPid);

        kit.Db.Patients.Single(p => p.PatientId == MomPid).SelfOpenid.Should().BeNull();
        kit.Db.Patients.Single(p => p.PatientId == DadPid).SelfOpenid.Should().Be(SisterOpenid);
        kit.Sister.SelfPatientId.Should().Be(DadPid);
        kit.Db.Patients.Count(p => p.SelfOpenid == SisterOpenid).Should().Be(1);
    }

    /// <summary>uid 传空串 = 解绑：两边一起清空，药单记录不动（历史还要留着给全家看）。</summary>
    [Fact]
    public async Task 传空uid就是解绑()
    {
        var kit = Build(PatientOf(DadPid, "爸爸", SisterOpenid));
        kit.Sister.SelfPatientId = DadPid;
        var handler = new FamilyBindMemberHandler(kit.Db, new MemberGuard(kit.Db, Clock()), Clock());

        await handler.HandleAsync(Fid, AdminOpenid, SisterOpenid, "");

        kit.Db.Patients.Single(p => p.PatientId == DadPid).SelfOpenid.Should().BeNull();
        kit.Sister.SelfPatientId.Should().BeNull();
    }

    /// <summary>只能绑**这个家里**的档案：传别家的 pid 等于跨家庭接管药单。</summary>
    [Fact]
    public async Task 不能绑别的家庭的档案()
    {
        var kit = Build(PatientOf(DadPid, "爸爸"));
        var handler = new FamilyBindMemberHandler(kit.Db, new MemberGuard(kit.Db, Clock()), Clock());

        var act = () => handler.HandleAsync(Fid, AdminOpenid, SisterOpenid, "P_other_family");

        await act.Should().ThrowAsync<ValidationException>();
        kit.Sister.SelfPatientId.Should().BeNull();
    }

    /// <summary>绑定**不改 role**（契约 §4.1）：管理员只是指出"这条药单是谁的"，不顺手提权/降权。</summary>
    [Fact]
    public async Task 绑定不改成员的权限角色()
    {
        var kit = Build(PatientOf(DadPid, "爸爸"));
        var handler = new FamilyBindMemberHandler(kit.Db, new MemberGuard(kit.Db, Clock()), Clock());

        await handler.HandleAsync(Fid, AdminOpenid, SisterOpenid, DadPid);

        kit.Sister.Role.Should().Be("member");
    }

    /// <summary>普通成员不能给别人绑药单。</summary>
    [Fact]
    public async Task 非管理员不能绑定()
    {
        var kit = Build(PatientOf(DadPid, "爸爸"));
        var handler = new FamilyBindMemberHandler(kit.Db, new MemberGuard(kit.Db, Clock()), Clock());

        var act = () => handler.HandleAsync(Fid, SisterOpenid, SisterOpenid, DadPid);

        await act.Should().ThrowAsync<ForbiddenException>();
        kit.Db.Patients.Single(p => p.PatientId == DadPid).SelfOpenid.Should().BeNull();
    }

    /// <summary>
    /// ⛔ 管理员不能用「绑定服药人」给自己绑一条别人的档案。
    /// 这正是 2026-11-02 事故的形状：在自己那行点「绑定服药人」→ 选「爸爸」→
    /// 爸爸的药变成"我的药"，周报/历史/打卡全错位，而界面每一步都像正常操作。
    /// 自己要吃药走首页「我自己吃药」（family.join role=patient），那条路只认领/新建属于本人的档案。
    /// </summary>
    [Fact]
    public async Task 不能给自己绑别人的档案()
    {
        var kit = Build(PatientOf(DadPid, "爸爸"));
        var handler = new FamilyBindMemberHandler(kit.Db, new MemberGuard(kit.Db, Clock()), Clock());

        // admin 在自己那一行（targetOpenid == openid）绑「爸爸」那条
        var act = () => handler.HandleAsync(Fid, AdminOpenid, AdminOpenid, DadPid);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().Contain("我自己吃药");
        kit.Db.Patients.Single(p => p.PatientId == DadPid).SelfOpenid.Should().BeNull();
        kit.Admin.SelfPatientId.Should().BeNull();
    }
}
