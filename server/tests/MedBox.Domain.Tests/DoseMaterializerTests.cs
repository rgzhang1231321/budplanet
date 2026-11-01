using FluentAssertions;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;

namespace MedBox.Domain.Tests;

/// <summary>
/// 剂量行展开的纯函数规则（不连库）：dose_id 确定性、剂量取 slot 快照、周期结束不再展开、重复时间点只出一行。
/// 幂等（ON CONFLICT DO NOTHING）依赖的正是"同一输入永远算出同一批 dose_id"，所以这里断的是规则不是死值。
/// </summary>
public class DoseMaterializerTests
{
    private const string FamilyId = "f1";
    private const string Date = "2026-11-01";
    private const long NowMs = 1_761_955_200_000L;

    [Fact]
    public void dose_id_is_deterministic_from_patient_date_slot()
    {
        var rows = DoseMaterializer.BuildRows(FamilyId, Date, [Plan(["08:00"])], Meds(), NowMs);

        rows.Should().ContainSingle();
        rows[0].DoseId.Should().Be($"{rows[0].PatientId}_{Date}_480");
        rows[0].Slot.Should().Be(480);
        rows[0].DueTs.Should().Be(TimeCn.SlotUtcMs(Date, 480));
        rows[0].Status.Should().Be(DoseStatusWire.Pending);
    }

    [Fact]
    public void same_input_yields_same_rows_so_repeat_materialize_is_a_no_op()
    {
        var plans = new[] { Plan(["08:00", "12:30"]), Plan(["08:00"], planId: "plan2", medicineId: "m2") };

        var first = DoseMaterializer.BuildRows(FamilyId, Date, plans, Meds(), NowMs);
        var second = DoseMaterializer.BuildRows(FamilyId, Date, plans, Meds(), NowMs);

        first.Select(r => r.DoseId).Should().BeEquivalentTo(second.Select(r => r.DoseId));
        first.Should().OnlyHaveUniqueItems(r => r.DoseId);
    }

    [Fact]
    public void duplicate_slot_in_one_batch_is_collapsed_to_one_row()
    {
        // 同一患者同一 slot 的两条计划（不同药）会算成同一个 dose_id，主键只能有一条
        var plans = new[] { Plan(["08:00"], planId: "planA", medicineId: "m1"), Plan(["08:00"], planId: "planB", medicineId: "m2") };

        var rows = DoseMaterializer.BuildRows(FamilyId, Date, plans, Meds(), NowMs);

        rows.Should().ContainSingle();
        rows[0].PlanId.Should().Be("planA"); // 先到先得，不覆盖已存在的那条
    }

    [Fact]
    public void dose_text_prefers_slot_snapshot_then_medicine_default()
    {
        var withSnapshot = Plan(["08:00"], planId: "planA");
        var withoutSnapshot = new Plan
        {
            PlanId = "planB",
            FamilyId = FamilyId,
            PatientId = "p1",
            MedicineId = "m1",
            StartDate = "2026-10-01",
            Status = "active",
            Slots = "[]",
            TimesMinute = [1200],
        };
        var meds = new Dictionary<string, Medicine> { ["m1"] = new() { MedicineId = "m1", Name = "苯磺酸氨氯地平片", DefaultDose = "1片" } };

        var rows = DoseMaterializer.BuildRows(FamilyId, Date, [withSnapshot, withoutSnapshot], meds, NowMs);

        rows.Should().HaveCount(2);
        rows.Single(r => r.Slot == 480).DoseText.Should().Be("半片");  // slot 快照优先
        rows.Single(r => r.Slot == 1200).DoseText.Should().Be("1片");  // 该 slot 没快照 → 回落药品默认剂量
    }

    [Fact]
    public void plan_ended_before_the_day_contributes_nothing()
    {
        var p = Plan(["08:00"]);
        p.CycleEndDate = "2026-10-31";

        DoseMaterializer.BuildRows(FamilyId, Date, [p], Meds(), NowMs).Should().BeEmpty();
    }

    [Fact]
    public void plan_ending_exactly_today_still_contributes()
    {
        var p = Plan(["08:00"]);
        p.CycleEndDate = Date; // 最后一天仍要吃

        DoseMaterializer.BuildRows(FamilyId, Date, [p], Meds(), NowMs).Should().ContainSingle();
    }

    private static Dictionary<string, Medicine> Meds() =>
        new() { ["m1"] = new Medicine { MedicineId = "m1", Name = "阿司匹林肠溶片", DefaultDose = "1片" } };

    private static Plan Plan(string[] times, string planId = "plan1", string medicineId = "m1")
    {
        var slots = string.Join(",", times.Select(t => $"{{\"t\":\"{t}\",\"d\":\"{Dose(t)}\"}}"));
        return new Plan
        {
            PlanId = planId,
            FamilyId = FamilyId,
            PatientId = "p1",
            MedicineId = medicineId,
            StartDate = "2026-10-01",
            Status = "active",
            Slots = $"[{slots}]",
            TimesMinute = times.Select(t => { TimeCn.TryParseHhMm(t, out var m); return m; }).ToArray(),
        };
    }

    /// <summary>只有 08:00 的 slot 写了剂量快照，其余走药品默认剂量。</summary>
    private static string Dose(string t) => t == "08:00" ? "半片" : "";
}
