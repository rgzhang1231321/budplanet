using FluentAssertions;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;

namespace MedBox.Domain.Tests;

/// <summary>
/// 剂量行物化时写入 week_key 的规则。week_key 由 due_date 同源推导（东八区 ISO 周），
/// 不写它 ix_dose_logs_week (patient_id, week_key) 就是一条永远命不中的死索引。
/// </summary>
public class DoseWeekKeyTests
{
    private const string FamilyId = "f1";
    private const long NowMs = 1_761_955_200_000L;

    [Fact]
    public void every_row_carries_the_iso_week_of_its_own_due_date()
    {
        var rows = DoseMaterializer.BuildRows(FamilyId, "2026-11-01", [Plan(["08:00", "12:30"])], Meds(), NowMs);

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.WeekKey == TimeCn.WeekKey(r.DueDate));
    }

    [Fact]
    public void all_rows_of_one_patient_day_share_one_week_key()
    {
        var rows = DoseMaterializer.BuildRows(FamilyId, "2026-11-01", [Plan(["08:00", "20:00"])], Meds(), NowMs);

        rows.Select(r => r.WeekKey).Distinct().Should().ContainSingle();
    }

    [Fact]
    public void sunday_and_the_following_monday_fall_into_different_weeks()
    {
        var sunday = DoseMaterializer.BuildRows(FamilyId, "2026-11-01", [Plan(["08:00"])], Meds(), NowMs);
        var monday = DoseMaterializer.BuildRows(FamilyId, "2026-11-02", [Plan(["08:00"])], Meds(), NowMs);

        sunday[0].WeekKey.Should().NotBe(monday[0].WeekKey); // 周界由 ISO 周决定，不按自然月切
    }

    [Fact]
    public void week_key_follows_due_date_not_the_materialize_time()
    {
        // 补跑历史日期（停机后回补）时，week_key 必须跟着 due_date，不能跟着"今天"
        var rows = DoseMaterializer.BuildRows(FamilyId, "2026-10-26", [Plan(["08:00"])], Meds(), NowMs);

        rows[0].WeekKey.Should().Be(TimeCn.WeekKey("2026-10-26"));
        rows[0].WeekKey.Should().NotBe(TimeCn.WeekKey(TimeCn.DateCn(NowMs)));
    }

    private static Dictionary<string, Medicine> Meds() =>
        new() { ["m1"] = new Medicine { MedicineId = "m1", Name = "阿司匹林肠溶片", DefaultDose = "1片" } };

    private static Plan Plan(string[] times) => new()
    {
        PlanId = "plan1",
        FamilyId = FamilyId,
        PatientId = "p1",
        MedicineId = "m1",
        StartDate = "2026-10-01",
        Status = "active",
        Slots = "[]",
        TimesMinute = times.Select(t => { TimeCn.TryParseHhMm(t, out var m); return m; }).ToArray(),
    };
}
