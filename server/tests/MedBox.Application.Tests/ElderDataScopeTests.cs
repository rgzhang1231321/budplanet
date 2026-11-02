using FluentAssertions;
using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Application.Tests.TestSupport;
using MedBox.Application.UseCases;
using MedBox.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MedBox.Application.Tests;

/// <summary>
/// ★ 2026-11-02 事故的**老人端同形**护栏：role=elder 却还没有绑上自己的服药人（selfUids 为空）时，
/// 四个数据出口必须一律返回空 —— <b>绝不能因为"手上没有 pid 可过滤"就把全家人的药下发</b>。
///
/// 旧口径在这四处都挂着 `&amp;&amp; SelfUids.Length > 0` 这个前提，等于"没法过滤 → 干脆不过滤"；
/// 而受邀老人第一次进家时恰好不绑 self（family.join 新成员分支的缺口），两条叠在一起就是：
/// 老人手机里出现全家的药名，他一点"已服"还会写进别人那条。
/// 根因已在 family.join 补上（进家即绑），本文件钉住第二层：判据只看 role。
///
/// 药品列表那一条同时钉住越权口子：elder 传别人的 uid 必须被忽略，不能"传谁的 pid 就取谁的药"。
/// </summary>
public sealed class ElderDataScopeTests
{
    private const string Fid = "F00000000001";
    private const string ElderOpenid = "openid_elder_unbound";
    private const string OtherPid = "P0000000001";
    private const string MinePid = "P0000000009";
    private const string Today = "2026-11-02";
    private const long Now = 1_761_955_200_000L;

    private static IClock Clock()
    {
        var clock = Substitute.For<IClock>();
        clock.NowMs.Returns(Now);
        clock.TodayCn.Returns(Today);
        return clock;
    }

    private static FamilyMember Member(string role, string openid) => new()
    {
        FamilyId = Fid,
        Openid = openid,
        Role = role,
        IsPatient = role == "elder",
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

    private static DoseLog DoseOf(string pid, string doseText = "1片") => new()
    {
        DoseId = $"{pid}_{Today}_480",
        FamilyId = Fid,
        PatientId = pid,
        PlanId = "PL0000000001",
        MedicineId = "M_" + pid,
        MedNameSnapshot = "苯磺酸氨氯地平片",
        DoseText = doseText,
        DueDate = Today,
        Slot = 480,
        DueTs = Now,
        Status = "pending",
        CreatedAt = Now,
    };

    private static Medicine MedicineOf(string pid) => new()
    {
        MedicineId = "M_" + pid,
        FamilyId = Fid,
        PatientId = pid,
        Name = "苯磺酸氨氯地平片",
        DefaultDose = "1片",
        Status = "active",
        CreatedAt = Now,
    };

    private sealed record Kit(IAppDbContext Db, IClock Clock, DoseMaterializer Materializer, IMemberGuard Guard);

    /// <summary>
    /// 家里有"爸爸"（另一位服药人）的一条今日剂量行 + 一个在售药品，而当前调用方是
    /// <b>selfUids 为空的 elder</b>。老代码在这种情况下不过滤 → 这四个接口都会把爸爸的东西吐出来。
    /// </summary>
    private static Kit BuildUnboundElder()
    {
        var db = FakeAppDbContext.Create(
            members: [Member("elder", ElderOpenid)],
            patients: [PatientOf(OtherPid, "爸爸")],
            doseLogs: [DoseOf(OtherPid)],
            medicines: [MedicineOf(OtherPid)]);
        var clock = Clock();
        return new Kit(db, clock, new DoseMaterializer(db, clock, NullLogger<DoseMaterializer>.Instance),
            new MemberGuard(db, clock));
    }

    /// <summary>
    /// 要求②的第 1 层：role=elder + selfUids 空 → today 返回空行（不是全家的行）。
    /// </summary>
    [Fact]
    public async Task elder没绑服药人时今日药单返回空而不是全家人的药()
    {
        var kit = BuildUnboundElder();
        var handler = new TodayHandler(kit.Db, kit.Guard, kit.Materializer, kit.Clock, NullLogger<TodayHandler>.Instance);

        var dto = await handler.HandleAsync(Fid, Today, ElderOpenid);

        dto.Rows.Should().BeEmpty();
    }

    /// <summary>week：完成率 0、漏服清单空、家庭计数 0 —— 一行都不许漏出来。</summary>
    [Fact]
    public async Task elder没绑服药人时周报返回空()
    {
        var kit = BuildUnboundElder();
        var handler = new WeekHandler(kit.Db, kit.Guard, kit.Materializer, kit.Clock);

        var dto = await handler.HandleAsync(Fid, null, ElderOpenid);

        dto.Rate.Should().Be(0);
        dto.FamilyCount.Should().Be(0);
        dto.MissList.Should().BeEmpty();
    }

    /// <summary>report.export（近 30 天报告）：按药聚合空、漏服 0 —— 不能把人家的记录导进他手机。</summary>
    [Fact]
    public async Task elder没绑服药人时导出报告返回空()
    {
        var kit = BuildUnboundElder();
        var handler = new ReportExportHandler(kit.Db, kit.Guard, kit.Materializer, kit.Clock);

        var dto = await handler.HandleAsync(Fid, null, ElderOpenid);

        dto.Meds.Should().BeEmpty();
        dto.MedCount.Should().Be(0);
        dto.MissCount.Should().Be(0);
    }

    /// <summary>medicine.list：一条药都不给（含"按 uid 精确取"这条路）。</summary>
    [Fact]
    public async Task elder没绑服药人时药品列表返回空()
    {
        var kit = BuildUnboundElder();
        var handler = new MedicineListHandler(kit.Db, kit.Guard, kit.Clock);

        var dto = await handler.HandleAsync(Fid, null, ElderOpenid);

        dto.Items.Should().BeEmpty();
    }

    /// <summary>
    /// ★ 越权口子（原 `MedicineHandlers` 的 `&amp;&amp; string.IsNullOrEmpty(uid)`）：
    /// elder 已绑自己（P0000000009），但请求里把**别人的 pid** 当 uid 传上来 ——
    /// 旧实现会整段跳过 elder 过滤，直接把爸爸的药单返回给他。
    /// 现在 uid 只有落在自己的 selfUids 里才被采纳，其余忽略（不抛 1005，旧前端照常能用）。
    /// </summary>
    [Fact]
    public async Task elder药品列表传别人的uid会被忽略且只返回自己的药()
    {
        var db = FakeAppDbContext.Create(
            members: [Member("elder", ElderOpenid)],
            patients: [PatientOf(OtherPid, "爸爸"), PatientOf(MinePid, "我", ElderOpenid)],
            medicines: [MedicineOf(OtherPid), MedicineOf(MinePid)]);
        var clock = Clock();
        var handler = new MedicineListHandler(db, new MemberGuard(db, clock), clock);

        var dto = await handler.HandleAsync(Fid, OtherPid, ElderOpenid);

        dto.Items.Should().HaveCount(1);
        dto.Items[0].Uid.Should().Be(MinePid);
        dto.Items.Should().NotContain(x => x.Uid == OtherPid);
    }

    /// <summary>同一道闸的正面口径：elder 传**自己的** uid 时照常能精确取到自己的药（不误伤合法请求）。</summary>
    [Fact]
    public async Task elder药品列表传自己的uid时正常返回自己的药()
    {
        var db = FakeAppDbContext.Create(
            members: [Member("elder", ElderOpenid)],
            patients: [PatientOf(OtherPid, "爸爸"), PatientOf(MinePid, "我", ElderOpenid)],
            medicines: [MedicineOf(OtherPid), MedicineOf(MinePid)]);
        var clock = Clock();
        var handler = new MedicineListHandler(db, new MemberGuard(db, clock), clock);

        var dto = await handler.HandleAsync(Fid, MinePid, ElderOpenid);

        dto.Items.Should().HaveCount(1);
        dto.Items[0].Uid.Should().Be(MinePid);
    }
}
