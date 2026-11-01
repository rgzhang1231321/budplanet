using System.Text.Json;
using MedBox.Application.Abstractions;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MedBox.Infrastructure.Workers;

/// <summary>
/// StatsWorker —— 每天 00:30（东八区）写前一天的日指标到 stats_daily。
/// 只写聚合值，不含可识别个人的明细（合规：不做画像、不卖数据）。
/// </summary>
public sealed class StatsWorker(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<StatsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("StatsWorker 启动");
        try
        {
            await RunOnceAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动写指标失败");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
                var delay = DailySchedule.NextRunUtcMs(nowMs, 0, 30) - nowMs;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1000, delay)), ct);
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "写日指标失败");
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
        var statDate = TimeCn.DateCn(TimeCn.DayStartUtcMs(today) - 86_400_000L); // 统计昨天
        var now = clock.NowMs;

        var rows = await db.DoseLogs.Where(d => d.DueDate == statDate).ToListAsync(ct);
        var total = rows.Count;
        var taken = rows.Count(r => r.Status == DoseStatusWire.Taken && !r.IsCorrected);
        var missed = rows.Count(r => r.Status == DoseStatusWire.Missed);
        var manual = rows.Count(r => r.IsCorrected);

        var payloads = new Dictionary<string, object>
        {
            ["checkin"] = new { total, taken, missed, manual, rate = total == 0 ? 0 : (int)Math.Round(taken * 100.0 / total) },
            ["scale"] = new
            {
                families = await db.Families.CountAsync(ct),
                patients = await db.Patients.CountAsync(p => p.Status == "active", ct),
                users = await db.Users.CountAsync(ct),
            },
        };

        var existing = await db.StatsDaily.Where(s => s.StatDate == statDate).ToListAsync(ct);
        foreach (var kv in payloads)
        {
            var row = existing.FirstOrDefault(s => s.MetricKey == kv.Key);
            if (row is null)
            {
                row = new StatsDaily { StatDate = statDate, MetricKey = kv.Key, CreatedAt = now };
                db.Add(row);
            }
            row.MetricValue = JsonSerializer.Serialize(kv.Value);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("日指标已写：{Date} total={Total} taken={Taken} missed={Missed}", statDate, total, taken, missed);
        return payloads.Count;
    }
}
