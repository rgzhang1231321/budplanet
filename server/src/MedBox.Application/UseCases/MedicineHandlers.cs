using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.UseCases;

public sealed class MedicineListHandler(IAppDbContext db, IMemberGuard guard, IClock clock)
{
    public async Task<MedicineListResult> HandleAsync(string fid, string? uid, string openid, CancellationToken ct = default)
    {
        var m = await guard.AssertMemberAsync(fid, openid, ct);

        var q = db.Medicines.Where(x => x.FamilyId == fid && x.Status == "active");
        if (!string.IsNullOrEmpty(uid)) q = q.Where(x => x.PatientId == uid);
        // 老人端只拿自己的药，不是下发后前端隐藏
        if (m.IsElder && string.IsNullOrEmpty(uid)) q = q.Where(x => m.SelfUids.Contains(x.PatientId));

        var meds = await q.OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var mids = meds.Select(x => x.MedicineId).ToList();
        var plans = await db.Plans
            .Where(p => mids.Contains(p.MedicineId) && p.Status == "active")
            .ToListAsync(ct);

        return new MedicineListResult
        {
            Items = meds.Select(med =>
            {
                var plan = plans.FirstOrDefault(p => p.MedicineId == med.MedicineId);
                return new MedicineItemDto
                {
                    Mid = med.MedicineId,
                    Uid = med.PatientId,
                    Name = med.Name,
                    Spec = med.Spec ?? "",
                    DefaultDose = med.DefaultDose ?? "",
                    Note = med.Remark,
                    Active = true,
                    // 出参回前端认的 cycle_30；直接透传 days_n 会让"30 天"的 chip 回填不上
                    CycleType = PlanCycle.ToWire(plan?.CycleType, plan?.CycleDays),
                    Slots = plan is null
                        ? []
                        : DoseMaterializer.ParseSlots(plan.Slots)
                            .Select(s => new SlotDto { T = s.T, D = s.D ?? med.DefaultDose ?? "", SlotKey = "s" + s.Minute })
                            .ToList(),
                };
            }).ToList(),
            ServerTs = clock.NowMs,
        };
    }
}

public sealed class CommonMedsHandler(IAppDbContext db, IClock clock)
{
    public async Task<CommonMedResult> HandleAsync(CancellationToken ct = default)
    {
        var rows = await db.MedicinesCommon.OrderBy(x => x.Id).ToListAsync(ct);
        return new CommonMedResult
        {
            Items = rows.Select(x => new CommonMedDto { Name = x.Name, Spec = x.Spec, DefaultDose = x.DefaultDose }).ToList(),
            // 读接口必带 server_ts（契约 §一）：前端靠它做 server_ts + offset 的时钟补偿
            ServerTs = clock.NowMs,
        };
    }
}

/// <summary>免费 3 个，第 4 个起要激励视频名额（credits 来自 entitlements，MVP 暂记 0）。</summary>
public sealed class MedicineCreateHandler(IAppDbContext db, IMemberGuard guard, ConfigReader config, EntitlementService entitlements, ContentSecurityChecker security, IClock clock)
{
    public async Task<object> HandleAsync(string fid, string openid, string uid, string name, string? spec, string? defaultDose, string? note, CancellationToken ct = default)
    {
        await guard.AssertMemberAsync(fid, openid, ct, "admin", "member");
        if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("先填药名");

        // 药名与备注是 UGC 文本，入库前过内容安全（87014 命中即拒）
        await security.EnsureSafeAsync([name, note], openid, ct);

        var patient = await db.Patients.FirstOrDefaultAsync(p => p.FamilyId == fid && p.PatientId == uid && p.Status == "active", ct)
                      ?? throw new ValidationException("服药人不存在");

        var cfg = await config.GetAsync(ct);
        var owned = await db.Medicines.CountAsync(x => x.FamilyId == fid && x.Status == "active", ct);
        var bonus = await entitlements.MedSlotCreditsAsync(fid, ct);
        if (owned >= cfg.FreeMedLimit + bonus) throw new QuotaExceededException("免费药品名额已用完");

        var mid = "M" + Guid.NewGuid().ToString("N")[..10];
        db.Add(new Medicine
        {
            MedicineId = mid,
            FamilyId = fid,
            PatientId = patient.PatientId,
            Name = name.Trim(),
            Spec = spec,
            DefaultDose = string.IsNullOrWhiteSpace(defaultDose) ? "1片" : defaultDose,
            Remark = note,
            Status = "active",
            CreatedBy = openid,
            CreatedAt = clock.NowMs,
        });
        await db.SaveChangesAsync(ct);
        return new { mid };
    }
}

/// <summary>
/// medicine.update：改药品属性。改药名会同步"未到点且未打卡"的行内快照，
/// 否则今天下午的药还挂着旧名（老人按旧名找药）；已过点/已打卡的历史一律不动。
/// default_dose 只改"下次建计划时继承的默认值"，**不反向改写已有计划的 slot 剂量** ——
/// 剂量是安全信息，静默改掉一个已排好的计划，比"改了没立刻生效"危险得多。
/// </summary>
public sealed class MedicineUpdateHandler(IAppDbContext db, IMemberGuard guard, ContentSecurityChecker security, IClock clock)
{
    public async Task<object> HandleAsync(string fid, string openid, string uid, string mid, string name, string? spec, string? defaultDose, string? note, CancellationToken ct = default)
    {
        await guard.AssertMemberAsync(fid, openid, ct, "admin", "member");
        if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("先填药名");

        await security.EnsureSafeAsync([name, note], openid, ct);

        var med = await db.Medicines.FirstOrDefaultAsync(m => m.FamilyId == fid && m.MedicineId == mid && m.Status == "active", ct)
                  ?? throw new ValidationException("药品不存在");
        if (!string.IsNullOrEmpty(uid) && med.PatientId != uid) throw new ValidationException("这药不在这位服药人名下");

        var renamed = !string.Equals(med.Name, name.Trim(), StringComparison.Ordinal);
        med.Name = name.Trim();
        if (spec is not null) med.Spec = string.IsNullOrWhiteSpace(spec) ? null : spec.Trim();
        if (!string.IsNullOrWhiteSpace(defaultDose)) med.DefaultDose = defaultDose.Trim();
        if (note is not null) med.Remark = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        if (renamed)
        {
            var now = clock.NowMs;
            var future = await db.DoseLogs
                .Where(d => d.MedicineId == mid && d.Status == DoseStatusWire.Pending && d.DueTs > now)
                .ToListAsync(ct);
            foreach (var row in future) row.MedNameSnapshot = med.Name;
        }

        await db.SaveChangesAsync(ct);
        return new { mid = med.MedicineId };
    }
}

/// <summary>
/// medicine.archive：软删。同 patient.archive —— 结束计划 + 删未到点 pending，历史保留。
/// </summary>
public sealed class MedicineArchiveHandler(IAppDbContext db, IMemberGuard guard, IClock clock)
{
    public async Task<object> HandleAsync(string fid, string openid, string mid, CancellationToken ct = default)
    {
        await guard.AssertMemberAsync(fid, openid, ct, "admin", "member");

        var med = await db.Medicines.FirstOrDefaultAsync(m => m.FamilyId == fid && m.MedicineId == mid, ct)
                  ?? throw new ValidationException("药品不存在");
        if (med.Status == "archived") return new { ok = true }; // 幂等

        med.Status = "archived";

        var plans = await db.Plans.Where(p => p.MedicineId == mid && p.Status == "active").ToListAsync(ct);
        foreach (var pl in plans) pl.Status = "finished";

        var now = clock.NowMs;
        var stale = await db.DoseLogs
            .Where(d => d.MedicineId == mid && d.Status == DoseStatusWire.Pending && d.DueTs > now)
            .ToListAsync(ct);
        foreach (var row in stale) db.Remove(row);

        await db.SaveChangesAsync(ct);
        return new { ok = true };
    }
}
