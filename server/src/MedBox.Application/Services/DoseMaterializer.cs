using System.Text.Json;
using MedBox.Application.Abstractions;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.Services;

/// <summary>
/// 剂量行展开（MaterializerWorker 的同一份逻辑，先以"按需补齐"落地）。
/// 幂等：dose_id 是确定性主键，插入一律 <c>ON CONFLICT (dose_id) DO NOTHING</c> ——
/// 不用"先查有没有、没有再插"：首页与打卡页同时补齐时会撞主键，异常被中间件映射成 1009，
/// 用户看到的是"服务异常"而不是正常页面。
/// </summary>
public sealed class DoseMaterializer(IAppDbContext db, IClock clock)
{
    public Task<int> EnsureDayAsync(string familyId, string dateCn, CancellationToken ct = default)
        => EnsureDaysAsync(familyId, [dateCn], ct);

    /// <summary>
    /// 一次补齐多天（周视图）。固定 3 次往返：plans 一次、medicines 一次、批量 INSERT 一次，
    /// 不随天数增长 —— 逐天调 <see cref="EnsureDayAsync"/> 是 7×(2 次查询 + 1 次插入)。
    /// 未来日期仍然跳过（否则周报会把还没到的日子算进完成率分母）。
    /// </summary>
    public async Task<int> EnsureDaysAsync(string familyId, IReadOnlyList<string> datesCn, CancellationToken ct = default)
    {
        var targets = DaysToFill(datesCn, clock.TodayCn);
        if (targets.Count == 0) return 0;

        // 取到最晚那天为止已开始的计划：更晚开始的计划在这批日期里都不该生成
        var lastDate = targets.OrderBy(d => d, StringComparer.Ordinal).Last();

        var plans = await db.Plans
            .Where(p => p.FamilyId == familyId && p.Status == "active" && string.Compare(p.StartDate, lastDate) <= 0)
            .ToListAsync(ct);
        if (plans.Count == 0) return 0;

        var meds = await db.Medicines
            .Where(m => m.FamilyId == familyId)
            .ToDictionaryAsync(m => m.MedicineId, ct);

        var rows = BuildRowsForDates(familyId, targets, plans, meds, clock.NowMs);
        if (rows.Count == 0) return 0;

        return await db.InsertDoseLogsIgnoreConflictAsync(rows, ct);
    }

    /// <summary>
    /// 纯函数：这批日期里真正要补的是哪几天 —— 去掉空值、未来日期与重复项。
    /// 未来日期不该有记录，否则周报会把"还没到的日子"算进完成率分母。
    /// </summary>
    public static List<string> DaysToFill(IReadOnlyList<string> datesCn, string todayCn)
        => datesCn
            .Where(d => !string.IsNullOrEmpty(d) && string.CompareOrdinal(d, todayCn) <= 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// 纯函数：多天 × 计划 × 时间点 → 待插入的剂量行。
    /// 计划只在 <c>start_date &lt;= 当天</c> 时生效，所以要按日过滤，不能只看批次里的最大日期。
    /// </summary>
    public static List<DoseLog> BuildRowsForDates(
        string familyId,
        IReadOnlyList<string> datesCn,
        IReadOnlyList<Plan> plans,
        IReadOnlyDictionary<string, Medicine> meds,
        long nowMs)
    {
        var rows = new List<DoseLog>();
        foreach (var date in datesCn)
        {
            var active = plans.Where(p => string.CompareOrdinal(p.StartDate, date) <= 0).ToList();
            if (active.Count == 0) continue;
            rows.AddRange(BuildRows(familyId, date, active, meds, nowMs));
        }
        return rows;
    }

    /// <summary>
    /// 纯函数：计划 × 时间点 → 待插入的剂量行（不查库、不取时钟，可直接单测）。
    /// dose_id = ${pid}_${date}_${slot}，slot = due_minute(0–1439)。
    /// </summary>
    public static List<DoseLog> BuildRows(
        string familyId,
        string dateCn,
        IReadOnlyList<Plan> plans,
        IReadOnlyDictionary<string, Medicine> meds,
        long nowMs)
    {
        var rows = new List<DoseLog>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var plan in plans)
        {
            if (!string.IsNullOrEmpty(plan.CycleEndDate) && string.CompareOrdinal(plan.CycleEndDate, dateCn) < 0) continue;

            var slots = ParseSlots(plan.Slots);
            foreach (var slot in plan.TimesMinute)
            {
                var doseId = DoseId.Build(plan.PatientId, dateCn, slot);
                if (!seen.Add(doseId)) continue; // 同一批里重复的计划时间点只插一次

                meds.TryGetValue(plan.MedicineId, out var med);
                var doseText = slots.FirstOrDefault(s => s.Minute == slot)?.D ?? med?.DefaultDose ?? "";

                rows.Add(new DoseLog
                {
                    DoseId = doseId,
                    FamilyId = familyId,
                    PatientId = plan.PatientId,
                    PlanId = plan.PlanId,
                    MedicineId = plan.MedicineId,
                    DoseText = doseText,
                    MedNameSnapshot = med?.Name,
                    DueDate = dateCn,
                    Slot = slot,
                    DueTs = TimeCn.SlotUtcMs(dateCn, slot),
                    Status = DoseStatusWire.Pending,
                    CreatedAt = nowMs,
                });
            }
        }

        return rows;
    }

    /// <summary>plans.slots 展开后的一项：展示串 + 派生分钟数。</summary>
    public sealed record PlanSlot(string T, string? D, int Minute);

    public static List<PlanSlot> ParseSlots(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<PlanSlot>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var t = el.TryGetProperty("t", out var tv) ? tv.GetString() : null;
                if (!TimeCn.TryParseHhMm(t, out var minute)) continue;
                var d = el.TryGetProperty("d", out var dv) ? dv.GetString() : null;
                list.Add(new PlanSlot(t!, d, minute));
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
