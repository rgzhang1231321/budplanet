using System.Text;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace MedBox.Infrastructure.Logging;

/// <summary>
/// 消费 LogQueue，批量 INSERT 进 app_logs。
///
/// 三条硬约束：
/// 1. 不经过 EF —— 否则每次 INSERT 又会产生 EF 日志，日志写日志，递归。
/// 2. 不注入 ILogger —— 写失败再用 logger 记，会再进一次队列，同样递归。失败只打控制台。
/// 3. 失败必须吞掉 —— 日志系统挂掉不能把 API 拖下线。连续失败会退避，避免刷屏。
/// </summary>
public sealed class DbLogWriter(
    LogQueue queue,
    DbLogOptions opt,
    MedBoxConnection connection) : BackgroundService
{
    private const string Columns =
        "(ts_ms,level,level_num,source,category,event_id,message,exception,props,trace_id,http_method,http_path,status_code,openid,page)";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!opt.Enabled) return;

        Console.WriteLine($"[log] 日志落库已启用 → app_logs（min={opt.MinLevel}, batch={opt.BatchSize}）");

        var batch = new List<LogEntry>(opt.BatchSize);
        var reader = queue.Reader;
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                batch.Clear();
                while (batch.Count < opt.BatchSize && reader.TryRead(out var e)) batch.Add(e);

                if (batch.Count == 0)
                {
                    // 没日志就等一会儿；1 秒超时是为了让"队列丢弃计数"能及时打出来
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    wait.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        await reader.WaitToReadAsync(wait.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // 超时属正常
                    }
                    ReportDropped();
                    continue;
                }

                // 不足一批时稍等，让并发写进来的日志凑成一批（日志频繁时能把 INSERT 次数压下来）
                if (batch.Count < opt.BatchSize)
                {
                    await Task.Delay(150, stoppingToken);
                    while (batch.Count < opt.BatchSize && reader.TryRead(out var e)) batch.Add(e);
                }

                await FlushAsync(batch, stoppingToken);
                failures = 0;
                ReportDropped();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"[log] 写入 app_logs 失败（第 {failures} 次）：{ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine("[log] 提示：若表不存在，先执行 bash db/apply.sh 应用 V005");

                // 退避：5s → 10s → 30s → 60s 封顶。数据库长时间不可用时不刷屏、不空转
                var backoffMs = failures switch
                {
                    1 => 5_000,
                    2 => 10_000,
                    3 => 30_000,
                    _ => 60_000,
                };
                try
                {
                    await Task.Delay(backoffMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Console.WriteLine("[log] 日志落库已停止");
    }

    private void ReportDropped()
    {
        var dropped = queue.TakeDropped();
        if (dropped > 0)
            Console.Error.WriteLine($"[log] 队列已满，丢弃 {dropped} 条日志（写入跟不上或数据库不可用）");
    }

    private async Task FlushAsync(List<LogEntry> rows, CancellationToken ct)
    {
        var sql = BuildSql(rows.Count);

        await using var conn = new NpgsqlConnection(connection.Value);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 15 };

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            cmd.Parameters.AddWithValue($"t{i}", r.TsMs);
            cmd.Parameters.AddWithValue($"l{i}", r.Level);
            cmd.Parameters.AddWithValue($"n{i}", r.LevelNum);
            cmd.Parameters.AddWithValue($"s{i}", r.Source);
            cmd.Parameters.AddWithValue($"c{i}", (object?)r.Category ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"e{i}", (object?)r.EventId ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"m{i}", (object?)r.Message ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"x{i}", (object?)r.Exception ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"p{i}", (object?)r.PropsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"tr{i}", (object?)r.TraceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"hm{i}", (object?)r.HttpMethod ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"hp{i}", (object?)r.HttpPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"sc{i}", (object?)r.StatusCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"o{i}", (object?)r.Openid ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"pg{i}", (object?)r.Page ?? DBNull.Value);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string BuildSql(int rowCount)
    {
        var sb = new StringBuilder("INSERT INTO app_logs ").Append(Columns).Append(" VALUES ");
        for (var i = 0; i < rowCount; i++)
        {
            if (i > 0) sb.Append(',');
            // props 显式转 jsonb：Npgsql 把 string 推断成 text，不转会报类型不匹配
            sb.Append($"(@t{i},@l{i},@n{i},@s{i},@c{i},@e{i},@m{i},@x{i},@p{i}::jsonb,@tr{i},@hm{i},@hp{i},@sc{i},@o{i},@pg{i})");
        }
        return sb.ToString();
    }
}
