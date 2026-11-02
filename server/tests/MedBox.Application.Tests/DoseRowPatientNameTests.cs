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
/// ★ 2026-11-03 客户投诉的护栏：「历史记录只看到 已服和漏服，不知道是谁服药还是谁没服药。」
///
/// 根因不在数据、不在权限，在**展示层把权威信息交给了缓存**：`rows[]` 原本只有 `pid`，
/// 服药人姓名要前端拿 `pid` 去 `family.patients` 里反查；而"要不要显示人名"的判据
/// （`patients.length >= 2`）读的也是这份缓存。缓存空/半加载时两件事同时发生：
/// 人名静默变空串，记录照常渲染 —— 用户看到的就是"只有状态、没有主语"。
///
/// 本文件钉住正面口径：`schedule.today` 的**每一行都自带 `patient`**，前端不需要任何本地缓存。
/// 别名（爸爸/妈妈）优先，没有别名回落真实姓名；两位服药人的行各带各的，不许串。
/// </summary>
public sealed class DoseRowPatientNameTests
{
    private const string Fid = "F00000000001";
    private const string GuardianOpenid = "openid_guardian";
    private const string DadPid = "P0000000001";
    private const string WifePid = "P0000000002";
    private const string MomPid = "P0000000003";
    private const string Today = "2026-11-03";
    private const long Now = 1_761_955_200_000L;

    private static IClock Clock()
    {
        var clock = Substitute.For<IClock>();
        clock.NowMs.Returns(Now);
        clock.TodayCn.Returns(Today);
        return clock;
    }

    private static Patient PatientOf(string pid, string alias, string name) => new()
    {
        PatientId = pid,
        FamilyId = Fid,
        Name = name,
        Alias = alias,
        Status = "active",
        CreatedAt = Now,
    };

    private static DoseLog DoseOf(string pid, int slot = 480) => new()
    {
        DoseId = $"{pid}_{Today}_{slot}",
        FamilyId = Fid,
        PatientId = pid,
        PlanId = "PL0000000001",
        MedicineId = "M_" + pid,
        MedNameSnapshot = "苯磺酸氨氯地平片",
        DoseText = "1片",
        DueDate = Today,
        Slot = slot,
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

    private static async Task<TodayHandler> HandlerWith(IAppDbContext db, IClock clock)
    {
        var guard = new MemberGuard(db, clock);
        await Task.CompletedTask;
        return new TodayHandler(db, guard, new DoseMaterializer(db, clock, NullLogger<DoseMaterializer>.Instance),
            clock, NullLogger<TodayHandler>.Instance);
    }

    /// <summary>两位服药人各一行：每行的 `patient` 必须是**这一行自己的**那个人，不许串。</summary>
    [Fact]
    public async Task 每行都带自己的服药人别名()
    {
        var db = FakeAppDbContext.Create(
            members: [new FamilyMember { FamilyId = Fid, Openid = GuardianOpenid, Role = "admin", JoinedAt = Now }],
            patients: [PatientOf(DadPid, "爸爸", "张建国"), PatientOf(WifePid, "媳妇", "李梅")],
            doseLogs: [DoseOf(DadPid, 480), DoseOf(WifePid, 720)],
            medicines: [MedicineOf(DadPid), MedicineOf(WifePid)]);
        var clock = Clock();
        var handler = await HandlerWith(db, clock);

        var dto = await handler.HandleAsync(Fid, Today, GuardianOpenid);

        dto.Rows.Should().HaveCount(2);
        dto.Rows.Single(r => r.Pid == DadPid).Patient.Should().Be("爸爸");
        dto.Rows.Single(r => r.Pid == WifePid).Patient.Should().Be("媳妇");
        dto.Rows.Should().OnlyContain(r => !string.IsNullOrEmpty(r.Patient));
    }

    /// <summary>没有别名（家人没设称呼）时回落真实姓名 —— 不能因为别名为空就整行没主语。</summary>
    [Fact]
    public async Task 没有别名时回落姓名而不是空串()
    {
        var db = FakeAppDbContext.Create(
            members: [new FamilyMember { FamilyId = Fid, Openid = GuardianOpenid, Role = "admin", JoinedAt = Now }],
            patients: [PatientOf(MomPid, "", "王秀兰")],
            doseLogs: [DoseOf(MomPid)],
            medicines: [MedicineOf(MomPid)]);
        var clock = Clock();
        var handler = await HandlerWith(db, clock);

        var dto = await handler.HandleAsync(Fid, Today, GuardianOpenid);

        dto.Rows.Should().ContainSingle();
        dto.Rows[0].Patient.Should().Be("王秀兰");
    }

    /// <summary>
    /// 周报的 `missList[].patient` 走同一套取名口径（v1.8 之前只取 Alias，别名为空就漏名字）。
    /// </summary>
    [Fact]
    public async Task 周报漏服条目没有别名时也回落姓名()
    {
        var missed = DoseOf(MomPid);
        missed.Status = "missed";
        var db = FakeAppDbContext.Create(
            members: [new FamilyMember { FamilyId = Fid, Openid = GuardianOpenid, Role = "admin", JoinedAt = Now }],
            patients: [PatientOf(MomPid, "", "王秀兰")],
            doseLogs: [missed],
            medicines: [MedicineOf(MomPid)]);
        var clock = Clock();
        var handler = new WeekHandler(db, new MemberGuard(db, clock),
            new DoseMaterializer(db, clock, NullLogger<DoseMaterializer>.Instance), clock);

        var dto = await handler.HandleAsync(Fid, null, GuardianOpenid);

        dto.MissList.Should().ContainSingle();
        dto.MissList[0].Patient.Should().Be("王秀兰");
    }
}
