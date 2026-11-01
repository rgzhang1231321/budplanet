using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MedBox.Infrastructure.Workers;

/// <summary>
/// 每日 00:10（东八区）展开当日剂量行；启动时先补跑一次，避免重启后当天没有行可打卡。
/// 展开本身幂等（dose_id 是确定性主键），重复跑不会覆盖已有打卡。
/// </summary>
public sealed class MaterializerWorker(
    IServiceScopeFactory scopes,
    ILogger<MaterializerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("MaterializerWorker 启动");
        try
        {
            await RunOnceAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动补展开失败");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var clock = scope.ServiceProvider.GetRequiredService<IClock>();
                var delayMs = DailySchedule.NextRunUtcMs(clock.NowMs, 0, 10) - clock.NowMs;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1000, delayMs)), ct);
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "每日展开失败");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var materializer = scope.ServiceProvider.GetRequiredService<DoseMaterializer>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var today = clock.TodayCn;
        var now = clock.NowMs;

        // 周期药到期自动结束 —— 契约里 plan.finish 的语义就是"不等用户点，到期就停"。
        // 少了这一步，plans 会堆一堆僵尸 active：不再生成行，但永远是"在用"状态。
        var expired = await db.Plans
            .Where(p => p.Status == "active" && p.CycleEndDate != null && string.Compare(p.CycleEndDate, today) < 0)
            .ToListAsync(ct);
        if (expired.Count > 0)
        {
            foreach (var p in expired)
            {
                p.Status = "finished";
                p.UpdatedAt = now;
            }

            // 到期计划理论上不该有未到点的行（end_date 已过），删一遍防手改数据留下的孤儿
            var ids = expired.Select(p => p.PlanId).ToList();
            var stale = await db.DoseLogs
                .Where(d => ids.Contains(d.PlanId) && d.Status == DoseStatusWire.Pending && d.DueTs > now)
                .ToListAsync(ct);
            foreach (var row in stale) db.Remove(row);

            await db.SaveChangesAsync(ct);
            logger.LogInformation("周期到期自动结束：{Count} 个计划", expired.Count);
        }

        var fids = await db.Families.Select(f => f.FamilyId).ToListAsync(ct);

        var added = 0;
        foreach (var fid in fids)
            added += await materializer.EnsureDayAsync(fid, today, ct);

        logger.LogInformation("每日展开：{Families} 个家庭，新增 {Added} 条剂量行（{Date}）", fids.Count, added, today);
        return added;
    }
}
