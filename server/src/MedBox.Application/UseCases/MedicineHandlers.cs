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
        // 带上 paused 计划：药品详情/列表要显示「已暂停 · 8月20日恢复」。
        var plans = await db.Plans
            .Where(p => mids.Contains(p.MedicineId) && p.Status != "finished")
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
                    PlanStatus = plan?.Status ?? "active",
                    PauseUntil = plan?.PauseUntil,
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

/// <summary>
/// medicine.suggest：P22 药名输入联想 + 「常用药」chips 的唯一数据源。
///
/// - `q` 为空 → **全平台引用次数 Top5**（`medicine_usage_stats`，净次数 ≥ 门槛才上榜），
///   不足 5 条用平台预设补位 —— 新库第一眼看到的仍是"苯磺酸氨氯地平片"这类入口，不是空白。
/// - `q` 非空 → 本家庭历史（含已归档，按最近添加）→ 全平台榜 → 平台预设，去重后最多 8 条。
///   自家药排最前：全平台榜会被更热门的药占满，把"我妈在吃的那一个"挤到看不见。
///
/// 与 `medicine.common` 的分工：那个是"平台预设、免登、给所有人"，这个是"全平台榜 + 你自己的历史、要鉴权"。
/// 不复用同一个端点是因为 common 是公开只读的（匿名可取），一旦挂上 fid 就得连带改鉴权模型。
/// </summary>
public sealed class MedicineSuggestHandler(IAppDbContext db, IMemberGuard guard, IClock clock)
{
    public async Task<MedicineSuggestResult> HandleAsync(string fid, string openid, string? q, CancellationToken ct = default)
    {
        await guard.AssertMemberAsync(fid, openid, ct);

        var kw = MedicineUsage.Normalize(q);
        var withKeyword = kw.Length > 0;
        var limit = withKeyword ? MedicineUsage.SuggestLimit : MedicineUsage.CommonTop;

        var presets = (await db.MedicinesCommon.OrderBy(x => x.Id).Take(20).ToListAsync(ct))
            .Where(x => MedicineUsage.Matches(x.Name, kw))
            .ToList();
        // 预设名本来就是公开数据（/api/medicine/common 匿名可取），它们上榜不需要过门槛
        var presetKeys = presets.Select(x => MedicineUsage.Normalize(x.Name)).ToHashSet(StringComparer.Ordinal);

        // 全平台榜：自定义药名要过门槛（见 MedicineUsage.MinCountToRank），联想只要 ≥1 就行
        var statRows = await db.MedicineUsageStats
            .Where(x => x.UseCount >= 1)
            .OrderByDescending(x => x.UseCount)
            .ThenByDescending(x => x.LastUsedAt)
            .ThenBy(x => x.Name)
            .Take(50)
            .Select(x => new { x.Name, x.Spec, x.DefaultDose, x.UseCount })
            .ToListAsync(ct);

        var ranked = statRows
            .Where(x => withKeyword || x.UseCount >= MedicineUsage.MinCountToRank
                        || presetKeys.Contains(MedicineUsage.Normalize(x.Name)))
            .Where(x => MedicineUsage.Matches(x.Name, kw))
            .ToList();

        var countOf = ranked
            .GroupBy(x => MedicineUsage.Normalize(x.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().UseCount, StringComparer.Ordinal);

        var global = ranked
            .Select(x => new MedicineSuggestDto { Name = x.Name, Spec = x.Spec, DefaultDose = x.DefaultDose, Count = x.UseCount });

        var groups = new List<IEnumerable<MedicineSuggestDto>>();
        if (withKeyword) groups.Add(await FamilyHistoryAsync(fid, kw, countOf, ct));
        groups.Add(global);
        groups.Add(presets.Select(x => new MedicineSuggestDto { Name = x.Name, Spec = x.Spec, DefaultDose = x.DefaultDose, Count = 0 }));

        return new MedicineSuggestResult
        {
            Items = MedicineUsage.Merge(groups, x => x.Name, limit),
            ServerTs = clock.NowMs,
        };
    }

    /// <summary>
    /// 本家庭的历史用药，含已归档：删掉的药往往正是过一阵要再添加的那一个。
    /// 顺序取"最近添加"，不按次数 —— 一个家庭就几个药，最近用过的最可能再用。
    /// </summary>
    private async Task<List<MedicineSuggestDto>> FamilyHistoryAsync(
        string fid, string kw, Dictionary<string, int> countOf, CancellationToken ct)
    {
        // Take(200) 只是防脏数据把响应撑爆的硬顶，家庭药品有免费名额限制，正常远小于这个数
        var rows = await db.Medicines
            .Where(x => x.FamilyId == fid)
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .Select(x => new { x.Name, x.Spec, x.DefaultDose })
            .ToListAsync(ct);

        return rows
            .Where(x => MedicineUsage.Matches(x.Name, kw))
            .Select(x => new MedicineSuggestDto
            {
                Name = x.Name,
                Spec = x.Spec,
                DefaultDose = x.DefaultDose,
                // 自家药也带全平台次数，前端"用过 N 次"才不会忽有忽无
                Count = countOf.TryGetValue(MedicineUsage.Normalize(x.Name), out var c) ? c : 0,
            })
            .ToList();
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
        var dose = string.IsNullOrWhiteSpace(defaultDose) ? "1片" : defaultDose;

        // 全平台引用次数 +1。放在建药之前：计数写失败就当这次没建。
        // 反过来（先建药再计数）会让用户看到"保存失败"却已经多了一个药，重试就重复占名额。
        await db.AddMedicineUsageAsync(MedicineUsage.Normalize(name), name.Trim(), spec, dose, 1, clock.NowMs, ct);

        db.Add(new Medicine
        {
            MedicineId = mid,
            FamilyId = fid,
            PatientId = patient.PatientId,
            Name = name.Trim(),
            Spec = spec,
            DefaultDose = dose,
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

        // 全平台引用次数 −1。放在归档成功之后：归档已经生效，计数失败最多让榜单偏高一次；
        // 反过来（先减后归档）一旦归档失败，就会出现"药还在、榜上却没它"。
        await db.AddMedicineUsageAsync(MedicineUsage.Normalize(med.Name), med.Name, med.Spec, med.DefaultDose, -1, now, ct);
        return new { ok = true };
    }
}
