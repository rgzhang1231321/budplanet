using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MedBox.Infrastructure.Workers;

/// <summary>
/// ReminderTickWorker —— 每 60s 一次，一件事但三个动作：
///   1) 漏服判定：过了 slot + missedThresholdMin 的 pending → missed（基准是 slot，不随 snooze 移动）
///   2) 到点提醒：刚过点的 pending → 入队 CHECKIN 推送（推给服药人本人）
///   3) 漏服告警：刚判 missed 且未告警 → 入队 MISSED 推送（推给家庭管理员，payload 不带药名）
/// 免打扰（默认 22:00–07:00）只压推送，不压漏服判定 —— 判定是记账，不是打扰。
/// </summary>
public sealed class ReminderTickWorker(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<ReminderTickWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var seconds = config.GetValue("Workers:ReminderTickSeconds", 60);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(2, seconds)));
        logger.LogInformation("ReminderTickWorker 启动，间隔 {Seconds}s", seconds);

        do
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "提醒/漏服 tick 失败");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task<(int missed, int reminders, int alerts)> TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var cfg = await scope.ServiceProvider.GetRequiredService<ConfigReader>().GetAsync(ct);
        var builder = scope.ServiceProvider.GetRequiredService<PushTaskBuilder>();

        var now = clock.NowMs;
        var missed = await MarkMissedAsync(db, clock, cfg.MissedThresholdMin, now, ct);

        // 免打扰时间只跳过推送，不跳过判定
        var quiet = QuietHours.Contains(cfg.QuietHours.Start, cfg.QuietHours.End, TimeCn.MinuteOfDay(now));
        if (quiet)
        {
            if (missed > 0) logger.LogInformation("漏服判定：{Count} 条置为 missed（免打扰中，暂不推送）", missed);
            return (missed, 0, 0);
        }

        var reminders = await builder.EnqueueDueRemindersAsync(ct);
        var alerts = await builder.EnqueueMissedAlertsAsync(ct);

        if (missed > 0 || reminders > 0 || alerts > 0)
            logger.LogInformation("tick：漏服 {Missed}，提醒入队 {Reminders}，告警入队 {Alerts}", missed, reminders, alerts);

        return (missed, reminders, alerts);
    }

    private async Task<int> MarkMissedAsync(IAppDbContext db, IClock clock, int thresholdMin, long now, CancellationToken ct)
    {
        var fromTs = TimeCn.DayStartUtcMs(TimeCn.DateCn(now - 3 * 86_400_000L));
        var tomorrowTs = TimeCn.DayStartUtcMs(clock.TodayCn) + 86_400_000L;

        var candidates = await db.DoseLogs
            .Where(d => d.Status == DoseStatusWire.Pending && d.DueTs >= fromTs && d.DueTs < tomorrowTs)
            .Select(d => new { d.DoseId, d.DueDate, d.Slot, d.IsCorrected })
            .ToListAsync(ct);

        var ids = candidates
            .Where(c => MissedPolicy.ShouldBeMissed(DoseStatusWire.Pending, c.IsCorrected, c.DueDate, c.Slot, now, thresholdMin))
            .Select(c => c.DoseId)
            .ToList();
        if (ids.Count == 0) return 0;

        // 只更新仍是 pending 的行：并发下已被打卡的不能被覆盖成 missed
        return await db.DoseLogs
            .Where(d => ids.Contains(d.DoseId) && d.Status == DoseStatusWire.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DoseStatusWire.Missed), ct);
    }
}
