using FluentAssertions;
using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Application.Tests.TestSupport;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MedBox.Application.Tests;

/// <summary>
/// DoseMaterializer 的"补齐幂等"边界（审计 §13.4 第 5 项的 Application 层那一半；
/// 真库 ON CONFLICT 的那一半在 MedBox.Integration.Tests.DoseMaterializerConcurrencyTests）。
///
/// 为什么不能"先查有没有、没有再插"：首页与打卡页会同时补齐同一天，
/// 两个请求撞 dose_logs 主键，异常被中间件映射成 1009 —— 用户看到的是"服务异常"而不是正常页面。
/// 所以插入一律走 ON CONFLICT (dose_id) DO NOTHING，**返回 0 行是正常结果，不是异常**。
/// </summary>
public sealed class DoseMaterializerIdempotencyTests
{
    private const string Fid = "F00000000001";
    private const string PatientId = "P0000000001";
    private const string MedicineId = "M0000000001";
    private const string PlanId = "PL0000000001";
    private const long Now = 1_761_955_200_000L;

    /// <summary>今天（东八区 2026-11-02 00:00 的 UTC 毫秒）。</summary>
    private static readonly long TodayMs =
        new DateTimeOffset(2026, 11, 1, 16, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static IClock Clock(string todayCn = "2026-11-02") => new FixedClock(Now, todayCn);

    private sealed class FixedClock(long nowMs, string todayCn) : IClock
    {
        public long NowMs { get; } = nowMs;
        public string TodayCn { get; } = todayCn;
    }

    private static Plan ActivePlan(string startDate = "2026-10-01") => new()
    {
        PlanId = PlanId,
        FamilyId = Fid,
        PatientId = PatientId,
        MedicineId = MedicineId,
        Slots = """[{"t":"08:00","d":"1片","slot_key":"s0800"},{"t":"20:00","d":"1片","slot_key":"s2000"}]""",
        TimesMinute = [480, 1200],
        CycleType = "long_term",
        StartDate = startDate,
        Status = "active",
        UpdatedAt = Now,
    };

    private static Medicine Medicine() => new()
    {
        MedicineId = MedicineId,
        FamilyId = Fid,
        PatientId = PatientId,
        Name = "苯磺酸氨氯地平片",
        DefaultDose = "1片",
        Status = "active",
        CreatedAt = Now,
    };

    private static DoseMaterializer Materializer(IAppDbContext db, IClock clock)
        => new(db, clock, NullLogger<DoseMaterializer>.Instance);

    // ── 未来日期被跳过 ────────────────────────────────────────

    /// <summary>未来日期不该有记录，否则周报会把"还没到的日子"算进完成率分母。</summary>
    [Fact]
    public async Task 未来日期被跳过不插入也不查计划()
    {
        var db = FakeAppDbContext.Create(plans: [ActivePlan()], medicines: [Medicine()])
            .WithIdempotentDoseInsert();
        var clock = Clock();

        var inserted = await Materializer(db, clock).EnsureDayAsync(Fid, "2099-01-01");

        inserted.Should().Be(0);
        _ = db.DidNotReceive().InsertDoseLogsIgnoreConflictAsync(
            Arg.Any<IReadOnlyList<DoseLog>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>多天批量里只有"今天及以前"的那几天会补，未来日期静默丢掉。</summary>
    [Fact]
    public async Task 多天批量里混未来日期时只补过去和今天()
    {
        var db = FakeAppDbContext.Create(plans: [ActivePlan()], medicines: [Medicine()])
            .WithIdempotentDoseInsert();
        var clock = Clock();

        var inserted = await Materializer(db, clock)
            .EnsureDaysAsync(Fid, ["2026-10-30", "2026-11-01", "2026-11-02", "2099-01-01"]);

        // 3 天 × 2 个时间点
        inserted.Should().Be(6);
    }

    [Fact]
    public void 纯函数DaysToFill去掉空值未来日期和重复项()
    {
        var days = DoseMaterializer.DaysToFill(
            ["2026-10-30", "", "2026-11-02", "2026-10-30", "2099-01-01"], "2026-11-02");

        days.Should().Equal("2026-10-30", "2026-11-02");
    }

    // ── 重复插入返回 0 行、不抛 ───────────────────────────────

    /// <summary>
    /// 同一天补两次：第二次全部撞 dose_id 主键，ON CONFLICT DO NOTHING 让它返回 0 行。
    /// "0 行"是正常结果，绝不能变成 1009。
    /// </summary>
    [Fact]
    public async Task 同一天补两次第二次返回0行且不抛()
    {
        var db = FakeAppDbContext.Create(plans: [ActivePlan()], medicines: [Medicine()])
            .WithIdempotentDoseInsert();
        var materializer = Materializer(db, Clock());

        (await materializer.EnsureDayAsync(Fid, "2026-11-01")).Should().Be(2);
        (await materializer.EnsureDayAsync(Fid, "2026-11-01")).Should().Be(0);
    }

    /// <summary>
    /// 首页与打卡页**同时**补齐同一天（两个并发请求）：
    /// 不抛异常、不产生重复 dose_id、总共只写入一次。
    /// 这是 ON CONFLICT (dose_id) DO NOTHING 存在的全部理由。
    /// </summary>
    [Fact]
    public async Task 两个并发EnsureDay不抛不产生重复dose_id()
    {
        var db = FakeAppDbContext.Create(plans: [ActivePlan()], medicines: [Medicine()])
            .WithIdempotentDoseInsert();
        var materializer = Materializer(db, Clock());

        var a = Task.Run(() => materializer.EnsureDayAsync(Fid, "2026-11-01"));
        var b = Task.Run(() => materializer.EnsureDayAsync(Fid, "2026-11-01"));
        var results = await Task.WhenAll(a, b);

        // 两个请求加起来只写入 2 行（一个时间点一行）；各自返回 0 或 2 都算合法
        results.Sum().Should().Be(2, "ON CONFLICT DO NOTHING 让其中一方整个批次落空");
        (await a).Should().BeInRange(0, 2);
        (await b).Should().BeInRange(0, 2);

        // 两次调用递交给插入的都是同样这 2 行 —— 去重发生在插入侧（替身按 dose_id 记账），
        // 所以"库里"不可能留下重复行，也不会抛主键冲突
        var batches = db.ReceivedCalls()
            .SelectMany(c => c.GetArguments())
            .OfType<IReadOnlyList<DoseLog>>()
            .ToList();
        batches.Should().HaveCount(2);
        foreach (var batch in batches)
            batch.Select(r => r.DoseId).Should().Equal(
                $"{PatientId}_2026-11-01_480", $"{PatientId}_2026-11-01_1200");
    }

    /// <summary>并发批量补一周同样不抛。</summary>
    [Fact]
    public async Task 两个并发EnsureDays一周不抛()
    {
        var db = FakeAppDbContext.Create(plans: [ActivePlan()], medicines: [Medicine()])
            .WithIdempotentDoseInsert();
        var materializer = Materializer(db, Clock());
        var week = new[] { "2026-10-27", "2026-10-28", "2026-10-29", "2026-10-30", "2026-10-31", "2026-11-01", "2026-11-02" };

        var act = async () => await Task.WhenAll(
            Task.Run(() => materializer.EnsureDaysAsync(Fid, week)),
            Task.Run(() => materializer.EnsureDaysAsync(Fid, week)));

        await act.Should().NotThrowAsync();
    }

    // ── 计划/药品边界的正常展开 ───────────────────────────────

    [Fact]
    public async Task 按计划时间点展开成确定性dose_id()
    {
        var db = FakeAppDbContext.Create(plans: [ActivePlan()], medicines: [Medicine()])
            .WithIdempotentDoseInsert();

        (await Materializer(db, Clock()).EnsureDayAsync(Fid, "2026-11-01")).Should().Be(2);

        var rows = db.ReceivedCalls()
            .SelectMany(c => c.GetArguments())
            .OfType<IReadOnlyList<DoseLog>>()
            .SelectMany(r => r)
            .ToList();
        rows.Select(r => r.DoseId).Should().Equal(
            $"{PatientId}_2026-11-01_480", $"{PatientId}_2026-11-01_1200");
        rows.Should().OnlyContain(r => r.Status == "pending");
        rows.Should().OnlyContain(r => r.DoseText == "1片");
        rows.Should().OnlyContain(r => r.WeekKey == "2026-W44");
    }

    /// <summary>更晚开始的计划在这批日期里都不该生成。</summary>
    [Fact]
    public async Task 计划还没开始的日期不生成()
    {
        var db = FakeAppDbContext.Create(plans: [ActivePlan(startDate: "2026-11-02")], medicines: [Medicine()])
            .WithIdempotentDoseInsert();

        (await Materializer(db, Clock()).EnsureDayAsync(Fid, "2026-11-01")).Should().Be(0);
        (await Materializer(db, Clock()).EnsureDayAsync(Fid, "2026-11-02")).Should().Be(2);
    }

    /// <summary>没有 active 计划时直接返回 0，不发 INSERT。</summary>
    [Fact]
    public async Task 没有active计划时不插入()
    {
        var paused = ActivePlan();
        paused.Status = "paused";
        var db = FakeAppDbContext.Create(plans: [paused], medicines: [Medicine()])
            .WithIdempotentDoseInsert();

        (await Materializer(db, Clock()).EnsureDayAsync(Fid, "2026-11-01")).Should().Be(0);
        _ = db.DidNotReceive().InsertDoseLogsIgnoreConflictAsync(
            Arg.Any<IReadOnlyList<DoseLog>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>plans.slots 坏掉时按"无快照"展开（剂量回落药品默认值），不抛异常。</summary>
    [Fact]
    public async Task slots是脏数据时按无快照展开不抛()
    {
        var broken = ActivePlan();
        broken.Slots = "{}";   // 不是数组，EnumerateArray 会抛 InvalidOperationException
        var db = FakeAppDbContext.Create(plans: [broken], medicines: [Medicine()])
            .WithIdempotentDoseInsert();

        (await Materializer(db, Clock()).EnsureDayAsync(Fid, "2026-11-01")).Should().Be(2);

        var rows = db.ReceivedCalls()
            .SelectMany(c => c.GetArguments())
            .OfType<IReadOnlyList<DoseLog>>()
            .SelectMany(r => r)
            .ToList();
        rows.Should().OnlyContain(r => r.DoseText == "1片", "回落药品默认剂量");
    }

    /// <summary>暂停到期自动恢复：物化某天时把到期日 <= 该天的 paused 计划翻回 active。</summary>
    [Fact]
    public async Task 暂停到期的计划在物化时自动恢复()
    {
        var paused = ActivePlan(startDate: "2026-10-01");
        paused.Status = "paused";
        paused.PauseUntil = "2026-11-01";
        var db = FakeAppDbContext.Create(plans: [paused], medicines: [Medicine()])
            .WithIdempotentDoseInsert();

        (await Materializer(db, Clock()).EnsureDayAsync(Fid, "2026-11-01")).Should().Be(2);
        paused.Status.Should().Be("active");
        paused.PauseUntil.Should().BeNull();
    }

    /// <summary>还没到期的暂停计划不恢复、不生成。</summary>
    [Fact]
    public async Task 暂停未到期的计划不恢复不生成()
    {
        var paused = ActivePlan(startDate: "2026-10-01");
        paused.Status = "paused";
        paused.PauseUntil = "2026-12-01";
        var db = FakeAppDbContext.Create(plans: [paused], medicines: [Medicine()])
            .WithIdempotentDoseInsert();

        (await Materializer(db, Clock()).EnsureDayAsync(Fid, "2026-11-01")).Should().Be(0);
        paused.Status.Should().Be("paused");
    }
}
