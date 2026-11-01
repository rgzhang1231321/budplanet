using System.Text.Json;
using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.UseCases;

/// <summary>
/// plan.create / plan.update（同一实现：1 药品 : 1 计划）。
/// 改计划后必须整树重拉 —— 未到点的 pending 旧行在这里删掉，新行由 schedule.today 重新展开。
/// </summary>
public sealed class PlanUpsertHandler(IAppDbContext db, IMemberGuard guard, IClock clock)
{
    public async Task<PlanUpsertResult> HandleAsync(string mid, List<SlotDto> slots, string cycleType, int? cycleDays, string? startDate, string openid, CancellationToken ct = default)
    {
        var med = await db.Medicines.FirstOrDefaultAsync(m => m.MedicineId == mid, ct)
                  ?? throw new ValidationException("药品不存在");

        var m = await guard.AssertMemberAsync(med.FamilyId, openid, ct, "admin", "member");
        _ = m;

        if (slots is null || slots.Count == 0) throw new ValidationException("至少设一个时间点");

        var normalized = new List<object>();
        var minutes = new List<int>();
        foreach (var s in slots)
        {
            if (!TimeCn.TryParseHhMm(s.T, out var minute)) throw new ValidationException($"时间格式不对：{s.T}");
            if (minutes.Contains(minute)) throw new ValidationException("同一个时间点重复了");
            minutes.Add(minute);
            normalized.Add(new
            {
                t = TimeCn.HhMm(minute),
                d = string.IsNullOrWhiteSpace(s.D) ? (med.DefaultDose ?? "") : s.D,
                slot_key = string.IsNullOrWhiteSpace(s.SlotKey) ? "s" + minute : s.SlotKey,
            });
        }
        minutes.Sort();

        var today = clock.TodayCn;
        string start;
        if (string.IsNullOrWhiteSpace(startDate))
        {
            start = today;
        }
        else if (!TimeCn.TryParseDate(startDate, out start))
        {
            // startDate 会进 plans.start_date 与 nextFireAt 的换算，格式不对必须 1005，
            // 不能让它一路走到 ParseExact 变成 1009（前端会当服务异常无限重投）
            throw new ValidationException("startDate 格式应为 YYYY-MM-DD（东八区自然日）");
        }
        var now = clock.NowMs;

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.MedicineId == mid && p.Status != "finished", ct);
        string planId;
        if (plan is null)
        {
            planId = "PL" + Guid.NewGuid().ToString("N")[..10];
            plan = new Plan
            {
                PlanId = planId,
                FamilyId = med.FamilyId,
                PatientId = med.PatientId,
                MedicineId = mid,
                StartDate = start,
                CreatedBy = openid,
                UpdatedAt = now,
            };
            db.Add(plan);
        }
        else
        {
            planId = plan.PlanId;
        }

        plan.Slots = JsonSerializer.Serialize(normalized);
        plan.TimesMinute = minutes.ToArray();
        // 前端传 cycle_30、后端存 days_n + cycle_days：归一在这一处，别让两个写法在系统里各活一半
        var (cycleKind, cycleLen) = PlanCycle.Parse(cycleType, cycleDays);
        plan.CycleType = cycleKind;
        plan.CycleDays = cycleLen;
        plan.CycleEndDate = PlanCycle.EndDate(start, cycleLen);
        plan.Status = "active";
        plan.UpdatedAt = now;

        // 未到点的 pending 旧行删掉（已 taken/missed/skipped 的不动，历史不可被改计划抹掉）
        var stale = await db.DoseLogs
            .Where(d => d.MedicineId == mid && d.Status == DoseStatusWire.Pending && d.DueTs > now)
            .ToListAsync(ct);
        foreach (var row in stale) db.Remove(row);

        await db.SaveChangesAsync(ct);

        return new PlanUpsertResult
        {
            PlanId = planId,
            // 基准日必须取 max(start, today)：计划从明天开始时，拿 today 算出来的"下次触发"
            // 是今天早上那个已经过去的时刻，前端倒计时会立刻变成负数。
            NextFireAt = TimeCn.SlotUtcMs(string.CompareOrdinal(start, today) > 0 ? start : today, minutes[0]),
        };
    }
}

/// <summary>
/// plan.finish：周期用药到期/主动结束。
/// 幂等（重复调用返回 ok），并删掉未到点的 pending 行 —— 计划停了还留着可打卡的行，
/// 老人会以为还要吃，子女会看到一个永远打不完的"待服"。
/// </summary>
public sealed class PlanFinishHandler(IAppDbContext db, IMemberGuard guard, IClock clock)
{
    public async Task<object> HandleAsync(string planId, string openid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(planId)) throw new ValidationException("缺少 planId");

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.PlanId == planId, ct)
                   ?? throw new ValidationException("计划不存在");

        await guard.AssertMemberAsync(plan.FamilyId, openid, ct, "admin", "member");
        if (plan.Status == "finished") return new { ok = true };

        plan.Status = "finished";
        plan.UpdatedAt = clock.NowMs;

        var now = clock.NowMs;
        var stale = await db.DoseLogs
            .Where(d => d.PlanId == planId && d.Status == DoseStatusWire.Pending && d.DueTs > now)
            .ToListAsync(ct);
        foreach (var row in stale) db.Remove(row);

        await db.SaveChangesAsync(ct);
        return new { ok = true };
    }
}
