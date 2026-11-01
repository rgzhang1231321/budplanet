using System.Text.Json;
using MedBox.Application.Abstractions;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.Services;

/// <summary>
/// 剂量行展开（MaterializerWorker 的同一份逻辑，先以"按需补齐"落地）。
/// 幂等：dose_id 是确定性主键，已存在就跳过，绝不覆盖用户打卡。
/// </summary>
public sealed class DoseMaterializer(IAppDbContext db, IClock clock)
{
    public async Task<int> EnsureDayAsync(string familyId, string dateCn, CancellationToken ct = default)
    {
        // 未来日期不该有记录：否则周报会把"还没到的日子"算进完成率分母
        if (string.CompareOrdinal(dateCn, clock.TodayCn) > 0) return 0;

        var existing = await db.DoseLogs
            .Where(x => x.FamilyId == familyId && x.DueDate == dateCn)
            .Select(x => x.DoseId)
            .ToListAsync(ct);
        var seen = new HashSet<string>(existing, StringComparer.Ordinal);

        var plans = await db.Plans
            .Where(p => p.FamilyId == familyId && p.Status == "active" && string.Compare(p.StartDate, dateCn) <= 0)
            .ToListAsync(ct);
        if (plans.Count == 0) return 0;

        var meds = await db.Medicines
            .Where(m => m.FamilyId == familyId)
            .ToDictionaryAsync(m => m.MedicineId, ct);

        var added = 0;
        foreach (var plan in plans)
        {
            if (!string.IsNullOrEmpty(plan.CycleEndDate) && string.CompareOrdinal(plan.CycleEndDate, dateCn) < 0) continue;

            var slots = ParseSlots(plan.Slots);
            foreach (var slot in plan.TimesMinute)
            {
                var doseId = DoseId.Build(plan.PatientId, dateCn, slot);
                if (seen.Contains(doseId)) continue;

                meds.TryGetValue(plan.MedicineId, out var med);
                var doseText = slots.FirstOrDefault(s => s.Minute == slot)?.D ?? med?.DefaultDose ?? "";

                db.Add(new DoseLog
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
                    CreatedAt = clock.NowMs,
                });
                seen.Add(doseId);
                added++;
            }
        }

        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
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
