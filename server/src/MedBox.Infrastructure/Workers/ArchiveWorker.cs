using MedBox.Application.Abstractions;
using MedBox.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MedBox.Infrastructure.Workers;

/// <summary>
/// ArchiveWorker —— 每天 03:30（东八区）清 90 天前的剂量明细。
/// 隐私政策承诺"服药明细默认保留 90 天"；聚合值（weekly_stats / stats_daily）不动。
/// 解锁了"历史明细永久保存"的家庭跳过（families.history_forever_at 非空）。
/// </summary>
public sealed class ArchiveWorker(
    IServiceScopeFactory scopes,
    Logging.MedBoxConnection connection,
    ILogger<ArchiveWorker> logger) : BackgroundService
{
    private const int RetainDays = 90;

    /// <summary>运行日志保留 30 天：排查问题够用，再久也没有分析价值，只会把库撑大。</summary>
    private const int LogRetainDays = 30;

    /// <summary>分批删，避免一次 DELETE 几十万行造成长事务、把写入卡住。</summary>
    private const int LogDeleteBatch = 5000;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("ArchiveWorker 启动");
        try
        {
            await RunOnceAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动清理失败");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var delay = DailySchedule.NextRunUtcMs(nowMs, 3, 30) - nowMs;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1000, delay)), ct);
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "明细清理失败");
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var today = clock.TodayCn;
        var cutoff = TimeCn.DateCn(TimeCn.DayStartUtcMs(today) - RetainDays * 86_400_000L);

        // 家庭级"永久保存"豁免
        var forever = await db.Families
            .Where(f => f.HistoryForeverAt != null)
            .Select(f => f.FamilyId)
            .ToListAsync(ct);

        var deleted = await db.DoseLogs
            .Where(d => d.DueDate.CompareTo(cutoff) < 0 && !forever.Contains(d.FamilyId))
            .ExecuteDeleteAsync(ct);

        if (deleted > 0) logger.LogInformation("明细清理：删除 {Count} 条（早于 {Cutoff}）", deleted, cutoff);

        await CleanLogsAsync(clock.NowMs, ct);
        return deleted;
    }

    /// <summary>
    /// 清过期运行日志。走独立连接而不是 EF：app_logs 没有实体映射，
    /// 而且这条 DELETE 每删一批都会触发 EF 的 SQL 日志（虽然已被排除，但没必要绕这一圈）。
    /// 清理失败不影响明细归档——日志清理烂掉最多是表变大，绝不能让 ArchiveWorker 整体失败。
    /// </summary>
    private async Task CleanLogsAsync(long nowMs, CancellationToken ct)
    {
        var cutoff = nowMs - LogRetainDays * 86_400_000L;
        var total = 0;
        try
        {
            await using var conn = new NpgsqlConnection(connection.Value);
            await conn.OpenAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                await using var cmd = new NpgsqlCommand(
                    "DELETE FROM app_logs WHERE log_id IN (SELECT log_id FROM app_logs WHERE ts_ms < @c LIMIT @n)",
                    conn);
                cmd.Parameters.AddWithValue("c", cutoff);
                cmd.Parameters.AddWithValue("n", LogDeleteBatch);

                var n = await cmd.ExecuteNonQueryAsync(ct);
                total += n;
                if (n < LogDeleteBatch) break;
                await Task.Delay(200, ct);
            }

            if (total > 0) logger.LogInformation("日志清理：删除 {Count} 条（早于 {Days} 天前）", total, LogRetainDays);
        }
        catch (Exception ex)
        {
            // 表还没建（没跑 V005）也会落到这里，不让它冒泡
            logger.LogWarning(ex, "日志清理失败（不影响明细归档）");
        }
    }
}
