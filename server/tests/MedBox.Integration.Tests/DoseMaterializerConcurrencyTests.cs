using FluentAssertions;
using MedBox.Application.Services;
using MedBox.Infrastructure.Persistence;
using MedBox.Integration.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MedBox.Integration.Tests;

/// <summary>
/// DoseMaterializer 并发补齐（审计 §13.4 第 5 项的真库那一半；
/// Application 层的替身版在 MedBox.Application.Tests.DoseMaterializerIdempotencyTests）。
///
/// 这里验的是 <c>INSERT … ON CONFLICT (dose_id) DO NOTHING</c> 在**真 PostgreSQL** 上的行为：
/// 首页与打卡页同时补齐同一天，两个请求撞同一个确定性主键，
/// 靠的就是这条子句让其中一个返回 0 行而不抛异常。
/// 换成"先查有没有、没有再插"，异常会被中间件映射成 1009 —— 用户看到的是"服务异常"而不是正常页面。
/// </summary>
[Collection(MedBoxCollection.Name)]
public sealed class DoseMaterializerConcurrencyTests
{
    private readonly PostgresContainerFixture _fx;

    public DoseMaterializerConcurrencyTests(PostgresContainerFixture fx) => _fx = fx;

    private const int SlotMorning = 480;
    private const int SlotEvening = 1200;

    /// <summary>
    /// 计划的起始日。必须远在过去：物化按 <c>start_date &lt;= 当天</c> 过滤，
    /// 用"今天"当起始日的话，补昨天/上周的日子一行都不生成。
    /// </summary>
    private const string PlanStartDate = "2020-01-01";

    private MedBoxDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<MedBoxDbContext>()
            .UseNpgsql(_fx.ConnectionString)
            .Options;
        return new MedBoxDbContext(options);
    }

    private static DoseMaterializer NewMaterializer(MedBoxDbContext db, string todayCn)
        => new(db, new FixedClock(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), todayCn),
            NullLogger<DoseMaterializer>.Instance);

    private async Task<(string Fid, string Pid)> SeedPlanAsync(string tag, string startDate, int[] slots)
    {
        var fid = "F_dm_" + tag;
        var pid = "P_dm_" + tag;
        var mid = "M_dm_" + tag;

        await _fx.Seed.UserAsync("openid_dm_" + tag);
        await _fx.Seed.FamilyAsync(fid, "openid_dm_" + tag);
        await _fx.Seed.MemberAsync(fid, "openid_dm_" + tag, "admin");
        await _fx.Seed.PatientAsync(pid, fid);
        await _fx.Seed.MedicineAsync(mid, fid, pid);
        await _fx.Seed.PlanAsync("PL_dm_" + tag, fid, pid, mid, startDate, slots);

        return (fid, pid);
    }

    private async Task<int> EnsureWeekOnceAsync(string fid, IReadOnlyList<string> week, string today)
    {
        await using var db = NewDb();
        return await NewMaterializer(db, today).EnsureDaysAsync(fid, week);
    }

    private async Task<int> CountAsync(string fid, string date)
        => (int)await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM dose_logs WHERE family_id = @fid AND due_date = @date",
            _fx.P("fid", fid), _fx.P("date", date));

    // ── 并发不抛、不产生重复 dose_id ─────────────────────────

    [Fact]
    public async Task 两个并发EnsureDay不抛且不产生重复dose_id()
    {
        var today = Seeder.TodayCn();
        var (fid, pid) = await SeedPlanAsync("conc", today, [SlotMorning, SlotEvening]);

        // 每个并发一个独立的 DbContext —— 真实场景里这就是两个请求各自的作用域。
        // DbContext 不是线程安全的，共用一个会把"上下文正在配置中"的 InvalidOperationException
        // 抛出来，那验的是替身自己的问题，不是 ON CONFLICT 的行为。
        var a = Task.Run(() => EnsureOnceAsync(fid, today));
        var b = Task.Run(() => EnsureOnceAsync(fid, today));

        // 关键是"不抛"：ON CONFLICT DO NOTHING 让后到的一方整个批次落空而不是炸 1009
        await FluentActions.Awaiting(async () => await Task.WhenAll(a, b))
            .Should().NotThrowAsync();

        var results = await Task.WhenAll(a, b);
        results.Sum().Should().Be(2, "两个请求加起来只写入 2 行（两个时间点）");
        (await CountAsync(fid, today)).Should().Be(2, "库里正好 2 行，没有重复 dose_id");
    }

    private async Task<int> EnsureOnceAsync(string fid, string today)
    {
        await using var db = NewDb();
        return await NewMaterializer(db, today).EnsureDayAsync(fid, today);
    }

    /// <summary>更多并发同样不抛（首页 + 打卡页 + 周视图同时进来的极端情况）。</summary>
    [Fact]
    public async Task 八个并发EnsureDays一周不抛且不重复()
    {
        var today = Seeder.TodayCn();
        var (fid, _) = await SeedPlanAsync("conc8", PlanStartDate, [SlotMorning, SlotEvening]);

        var week = Enumerable.Range(0, 7)
            .Select(i => Seeder.TodayCn()) // 同一天并发更有杀伤力
            .Distinct()
            .ToList();
        // 再混几天过去的日期，凑够一周的跨度
        week = new[] { today }.Concat(
            Enumerable.Range(1, 6).Select(i =>
                MedBox.Domain.TimeCn.DateCn(
                    DateTimeOffset.UtcNow.AddDays(-i).ToUnixTimeMilliseconds()))).ToList();

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => EnsureWeekOnceAsync(fid, week, today)))
            .ToList();

        await FluentActions.Awaiting(async () => await Task.WhenAll(tasks))
            .Should().NotThrowAsync();

        var expected = week.Count * 2;
        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM dose_logs WHERE family_id = @fid", _fx.P("fid", fid)))
            .Should().Be(expected, "并发 8 次也只写入每个 (date, slot) 一行");

        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT dose_id FROM dose_logs WHERE family_id = @fid " +
            "GROUP BY dose_id HAVING count(*) > 1) d", _fx.P("fid", fid)))
            .Should().Be(0, "不存在重复 dose_id");
    }

    // ── 未来日期被跳过 ───────────────────────────────────────

    [Fact]
    public async Task 未来日期被跳过不插入()
    {
        var today = Seeder.TodayCn();
        var future = MedBox.Domain.TimeCn.DateCn(DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds());
        var (fid, _) = await SeedPlanAsync("future", PlanStartDate, [SlotMorning]);

        await using var db = NewDb();
        var materializer = NewMaterializer(db, today);

        (await materializer.EnsureDayAsync(fid, future)).Should().Be(0);
        (await CountAsync(fid, future)).Should().Be(0, "未来日期不该有剂量行，否则周报会把没到的日子算进完成率分母");
    }

    /// <summary>多天批量里混未来日期：只补过去和今天。</summary>
    [Fact]
    public async Task 多天批量里未来日期被静默丢掉()
    {
        var today = Seeder.TodayCn();
        var yesterday = MedBox.Domain.TimeCn.DateCn(DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds());
        var future = MedBox.Domain.TimeCn.DateCn(DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeMilliseconds());
        var (fid, _) = await SeedPlanAsync("mixfuture", PlanStartDate, [SlotMorning]);

        await using var db = NewDb();
        var materializer = NewMaterializer(db, today);

        (await materializer.EnsureDaysAsync(fid, [yesterday, today, future])).Should().Be(2);
        (await CountAsync(fid, future)).Should().Be(0);
        (await CountAsync(fid, today)).Should().Be(1);
    }

    // ── 确定性 dose_id / week_key ────────────────────────────

    [Fact]
    public async Task 生成的dose_id和week_key符合约定()
    {
        var today = Seeder.TodayCn();
        var (fid, pid) = await SeedPlanAsync("ids", today, [SlotMorning]);

        await using var db = NewDb();
        (await NewMaterializer(db, today).EnsureDayAsync(fid, today)).Should().Be(1);

        string doseId;
        string? weekKey;
        await using (var conn = await _fx.OpenAsync())
        await using (var cmd = new NpgsqlCommand(
            "SELECT dose_id, week_key FROM dose_logs WHERE family_id = @fid AND due_date = @date", conn))
        {
            cmd.Parameters.AddWithValue("fid", fid);
            cmd.Parameters.AddWithValue("date", today);
            await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue("刚物化出来应该有一行");
            doseId = reader.GetString(0);
            weekKey = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        // 契约：dose_id = ${pid}_${date}_${slot}，slot = due_minute
        doseId.Should().Be($"{pid}_{today}_{SlotMorning}");
        // week_key 必须非空：ix_dose_logs_week (patient_id, week_key) 与 V012 的 ix_dose_logs_week_key 靠它命中
        weekKey.Should().NotBeNullOrEmpty().And.MatchRegex("^20[0-9]{2}-W(0[1-9]|[1-4][0-9]|5[0-3])$");
    }
}
