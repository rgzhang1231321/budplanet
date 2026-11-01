using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MedBox.Infrastructure.Workers;

/// <summary>
/// WeekReportWorker —— 每周日 20:00（东八区）归档本周聚合到 weekly_stats，随后把周报推送入队。
/// 归档的意义：明细 90 天后会被 ArchiveWorker 清掉，周报还得能算出来。
/// 幂等：按 (family, patient, week_key) upsert，重复跑只覆盖不新增。
/// </summary>
public sealed class WeekReportWorker(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<WeekReportWorker> logger) : BackgroundService
{
    private string? _archivedWeek;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("WeekReportWorker 启动");

        // 启动先补一次"最近一个已结束的周"，避免停机错过周日 20:00
        try
        {
            await RunOnceAsync(clockNow(), archiveCurrentWeek: false, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动补归档失败");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), ct);
                await RunOnceAsync(clockNow(), archiveCurrentWeek: Due(clockNow()), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "周报归档失败");
            }
        }
    }

    private static long clockNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>本周日 20:00 之后就算"本周到点"。</summary>
    private static bool Due(long nowMs)
    {
        var now = TimeCn.ToCn(nowMs);
        return now.DayOfWeek == DayOfWeek.Sunday && now.Hour >= 20;
    }

    public async Task<int> RunOnceAsync(long nowMs, bool archiveCurrentWeek, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        var today = TimeCn.DateCn(nowMs);
        var weekStart = TimeCn.WeekStart(today);
        if (!archiveCurrentWeek)
            weekStart = TimeCn.WeekStart(TimeCn.DateCn(TimeCn.DayStartUtcMs(weekStart) - 86_400_000L));

        var weekKey = TimeCn.WeekKey(weekStart);
        if (_archivedWeek == weekKey) return 0;

        var dates = TimeCn.WeekDates(weekStart).ToList();
        var rows = await db.DoseLogs.Where(d => dates.Contains(d.DueDate)).ToListAsync(ct);
        if (rows.Count == 0)
        {
            _archivedWeek = weekKey;
            return 0;
        }

        var existing = await db.WeeklyStats.Where(w => w.WeekKey == weekKey).ToListAsync(ct);
        var count = 0;

        foreach (var g in rows.GroupBy(r => new { r.FamilyId, r.PatientId }))
        {
            var total = g.Count();
            var taken = g.Count(r => r.Status == DoseStatusWire.Taken && !r.IsCorrected);
            var missed = g.Count(r => r.Status == DoseStatusWire.Missed);
            var skipped = g.Count(r => r.Status == DoseStatusWire.Skipped);
            var manual = g.Count(r => r.IsCorrected);

            var stat = existing.FirstOrDefault(w => w.FamilyId == g.Key.FamilyId && w.PatientId == g.Key.PatientId);
            if (stat is null)
            {
                stat = new WeeklyStat { FamilyId = g.Key.FamilyId, PatientId = g.Key.PatientId, WeekKey = weekKey };
                db.Add(stat);
            }

            stat.Total = total;
            stat.TakenCnt = taken;
            stat.MissedCnt = missed;
            stat.SkippedCnt = skipped;
            stat.ManualCorrectCount = manual; // 单列，不进完成率
            stat.UpdatedAt = nowMs;
            count++;
        }

        await db.SaveChangesAsync(ct);
        _archivedWeek = weekKey;
        logger.LogInformation("周报归档：{Week} {Patients} 位服药人（{Rows} 条明细）", weekKey, count, rows.Count);

        // 归档完立刻把周报推送入队（WEEKLY 模板：time5 统计时间 + thing6 备注）。
        // 只在"这份周报还新鲜"时入队：周日 20:00 正常触发 delta<0；停机后补跑超过窗口就不补发旧周报——
        // 上周的完成率周一早上还值得看，隔一周再推只是噪音。窗口走配置，便于压测/补数据。
        var freshHours = config.GetValue("Workers:WeeklyPushFreshHours", 48);
        if (nowMs - TimeCn.WeekEndUtcMs(weekStart) >= freshHours * 3_600_000L) return count;

        try
        {
            var builder = scope.ServiceProvider.GetRequiredService<PushTaskBuilder>();
            var pushed = await builder.EnqueueWeeklyReportsAsync(weekKey, TimeCn.WeekRangeText(weekStart), ct);
            if (pushed > 0) logger.LogInformation("周报推送入队：{Week} {Families} 个家庭", weekKey, pushed);
        }
        catch (Exception ex)
        {
            // 归档已经落库，推送入队失败不该把这一次归档也算失败（下次 tick 会用 _archivedWeek 挡住重跑）
            logger.LogError(ex, "周报推送入队失败：{Week}", weekKey);
        }

        return count;
    }
}
