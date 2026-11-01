using FluentAssertions;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;

namespace MedBox.Domain.Tests;

/// <summary>
/// 周视图批量补齐的规则（纯函数，不连库）：只补"到得了"的日子、计划从 start_date 起才生效、
/// 批量结果必须等于逐天的并集。逐天 EnsureDayAsync 是 7×(2 次查询 + 1 次插入)，批量后固定 3 次往返。
/// </summary>
public class DoseMaterializerBatchTests
{
    private const string FamilyId = "f1";
    private const string Today = "2026-11-01";
    private const long NowMs = 1_761_955_200_000L;

    [Fact]
    public void a_whole_past_week_is_filled_day_by_day()
    {
        var rows = DoseMaterializer.BuildRowsForDates(FamilyId, TimeCn.WeekDates("2026-10-26").ToList(), [Plan(["08:00"])], Meds(), NowMs);

        rows.Should().HaveCount(7);
        rows.Select(r => r.DoseId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void future_days_are_dropped_before_any_query()
    {
        var days = DoseMaterializer.DaysToFill(["2026-10-31", Today, "2026-11-02"], Today);

        days.Should().Equal("2026-10-31", Today);
    }

    [Fact]
    public void duplicate_and_blank_days_are_collapsed()
        => DoseMaterializer.DaysToFill([Today, "", Today, "2026-10-31"], Today)
            .Should().Equal(Today, "2026-10-31");

    [Fact]
    public void a_plan_starting_mid_week_only_fills_from_its_start_date()
    {
        var plan = Plan(["08:00"]);
        plan.StartDate = "2026-10-29"; // 周三开始

        var rows = DoseMaterializer.BuildRowsForDates(FamilyId, TimeCn.WeekDates("2026-10-26").ToList(), [plan], Meds(), NowMs);

        rows.Should().HaveCount(4); // 10-29 ~ 11-01
        rows.Should().OnlyContain(r => string.CompareOrdinal(r.DueDate, "2026-10-29") >= 0);
    }

    [Fact]
    public void batch_rows_are_the_union_of_per_day_rows()
    {
        // 批量与逐天必须算出同一批 dose_id，否则"周视图行数"会随调用方式变化
        var plans = new[] { Plan(["08:00", "12:30"]) };
        var days = TimeCn.WeekDates("2026-10-26").ToList();

        var batched = DoseMaterializer.BuildRowsForDates(FamilyId, days, plans, Meds(), NowMs);
        var perDay = days.SelectMany(d => DoseMaterializer.BuildRows(FamilyId, d, plans, Meds(), NowMs)).ToList();

        batched.Select(r => r.DoseId).Should().BeEquivalentTo(perDay.Select(r => r.DoseId));
    }

    private static Dictionary<string, Medicine> Meds() =>
        new() { ["m1"] = new Medicine { MedicineId = "m1", Name = "药", DefaultDose = "1片" } };

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
