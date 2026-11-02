using System.Text.Json;
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
/// PushWorker —— 消费 push_tasks，按 errcode 判决。
/// 幂等靠"预占"：queued→sending 用带条件的 UPDATE，抢不到说明别人在处理，直接跳过。
/// 判决：成功扣额度；43101/43004/40003 丢弃；45009 指数退避；其余重试到上限转 failed。
///
/// ⚠️ **分片当前未启用**（审计 §13.4.1，2026-11-02 决策 9）：下面的拉取谓词只按
/// status + next_retry_at 全表扫描，**不按 shard 过滤**，而写入侧 <see cref="PushPolicy.Shard"/>
/// 已经把任务稳定分配到了 shard。也就是说 shard 这一列今天只写不读，多实例部署时每个实例
/// 都会拉到同一批任务、靠 queued→sending 的条件 UPDATE 互相竞争（能保证不重发，但没有分片收益）。
/// 要启用分片必须同时改消费端谓词 + 回填存量 shard + 补漏消费监控，见 <see cref="PushPolicy.ShardCount"/>。
/// </summary>
public sealed class PushWorker(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<PushWorker> logger) : BackgroundService
{
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var seconds = config.GetValue("Workers:PushTickSeconds", 10);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(2, seconds)));
        logger.LogInformation("PushWorker 启动，间隔 {Seconds}s", seconds);

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
                logger.LogError(ex, "推送 tick 失败");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task<int> TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var cfg = await scope.ServiceProvider.GetRequiredService<ConfigReader>().GetAsync(ct);
        var wx = scope.ServiceProvider.GetRequiredService<IWxApiClient>();

        // 三个模板都没配就别空转（避免每 10 秒刷一条失败日志）
        if (string.IsNullOrWhiteSpace(cfg.TmplIds.Checkin)
            && string.IsNullOrWhiteSpace(cfg.TmplIds.Missed)
            && string.IsNullOrWhiteSpace(cfg.TmplIds.Weekly))
            return 0;

        var now = clock.NowMs;
        var batch = await db.PushTasks
            .Where(t => t.Status == "queued" && t.NextRetryAt <= now)
            .OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (batch.Count == 0) return 0;

        var handled = 0;
        foreach (var task in batch)
        {
            var claimed = await db.PushTasks
                .Where(t => t.TaskId == task.TaskId && t.Status == "queued")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "sending"), ct);
            if (claimed == 0) continue;

            var tmplId = task.TmplKey switch
            {
                "CHECKIN" => cfg.TmplIds.Checkin,
                "MISSED" => cfg.TmplIds.Missed,
                "WEEKLY" => cfg.TmplIds.Weekly,
                _ => "",
            };

            var data = new Dictionary<string, string>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(task.Payload) ? "{}" : task.Payload);
                data = TemplateData.For(task.TmplKey, doc.RootElement);
            }
            catch (JsonException)
            {
                // payload 坏了：不该重试，直接 failed
            }

            var page = task.TmplKey switch
            {
                "CHECKIN" => "pages/checkin/index",
                "WEEKLY" => "pages/weekly/index",
                _ => "pages/home/index",
            };

            var res = await wx.SendSubscribeAsync(task.Openid, tmplId, page, data, ct);
            var errCode = res.Ok ? 0 : res.ErrCode;
            var outcome = PushPolicy.Decide(errCode);
            var ts = clock.NowMs;

            switch (outcome)
            {
                case PushOutcome.Delivered:
                    await db.PushTasks.Where(t => t.TaskId == task.TaskId).ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.Status, "sent")
                              .SetProperty(t => t.Errcode, 0)
                              .SetProperty(t => t.SentAt, ts), ct);
                    await ConsumeQuotaAsync(db, task.Openid, task.TmplKey, ts, ct);
                    break;

                case PushOutcome.Drop:
                    await db.PushTasks.Where(t => t.TaskId == task.TaskId).ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.Status, "dropped")
                              .SetProperty(t => t.Errcode, errCode), ct);
                    if (errCode == PushPolicy.ErrInvalidOpenid)
                        await db.Users.Where(u => u.Openid == task.Openid)
                            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Unreachable, true), ct);
                    logger.LogInformation("推送丢弃 task={TaskId} errcode={Code}", task.TaskId, errCode);
                    break;

                case PushOutcome.Retry:
                    var retry = task.RetryCount + 1;
                    var next = ts + PushPolicy.RetryDelayMs(retry);
                    await db.PushTasks.Where(t => t.TaskId == task.TaskId).ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.Status, retry > PushPolicy.MaxRetry ? "failed" : "queued")
                              .SetProperty(t => t.RetryCount, (short)retry)
                              .SetProperty(t => t.NextRetryAt, next)
                              .SetProperty(t => t.Errcode, errCode), ct);
                    break;

                default: // Fail
                    await db.PushTasks.Where(t => t.TaskId == task.TaskId).ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.Status, "failed")
                              .SetProperty(t => t.Errcode, errCode), ct);
                    logger.LogWarning("推送失败（不重试）task={TaskId} errcode={Code} msg={Msg}", task.TaskId, errCode, res.ErrMsg);
                    break;
            }

            handled++;
        }

        return handled;
    }

    /// <summary>成功才扣额度：一次性订阅授权一次 = 一条，扣完就发不出去（前端不再弹授权就不能硬发）。</summary>
    private static async Task ConsumeQuotaAsync(IAppDbContext db, string openid, string tmplKey, long ts, CancellationToken ct)
    {
        await db.Subscriptions
            .Where(s => s.Openid == openid && s.TmplKey == tmplKey && s.Quota > 0)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Quota, x => x.Quota - 1)
                .SetProperty(x => x.UsedTotal, x => x.UsedTotal + 1)
                .SetProperty(x => x.LastUsedAt, ts), ct);
    }
}
