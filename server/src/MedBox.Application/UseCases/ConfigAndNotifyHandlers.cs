using System.Text.Json;
using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.UseCases;

/// <summary>config.get：冷启后异步拉一次，覆盖 login 缓存里可能过期的值（广告开关不发版即生效）。</summary>
public sealed class ConfigGetHandler(ConfigReader config, IClock clock)
{
    public async Task<object> HandleAsync(CancellationToken ct = default)
    {
        var cfg = await config.GetAsync(ct);
        return new { config = cfg, server_ts = clock.NowMs };
    }
}

/// <summary>notify.quota：进首页拉一次，前端缓存 5 分钟。</summary>
public sealed class QuotaHandler(IAppDbContext db, IClock clock)
{
    public async Task<QuotaResultDto> HandleAsync(string openid, CancellationToken ct = default)
    {
        var rows = await db.Subscriptions.Where(s => s.Openid == openid).ToListAsync(ct);
        int Get(string key) => rows.FirstOrDefault(r => r.TmplKey == key)?.Quota ?? 0;
        return new QuotaResultDto
        {
            Quota = new QuotaDto { Checkin = Get("CHECKIN"), Missed = Get("MISSED"), Weekly = Get("WEEKLY") },
            MainSwitch = rows.All(r => r.MainSwitch) || rows.Count == 0,
            ServerTs = clock.NowMs,
        };
    }
}

/// <summary>
/// notify.ack：必须在 requestSubscribeMessage 回调里立刻调，拒授权也要调一次
/// （拒绝数只有前端知道，不报则授权率永远 100%）。
/// 工具-效率类目只能一次性订阅：accepted 里有一个模板 = +1 条额度。
/// </summary>
public sealed class NotifyAckHandler(IAppDbContext db, IClock clock)
{
    public async Task<object> HandleAsync(AckRequest req, string openid, CancellationToken ct = default)
    {
        var now = clock.NowMs;
        // tmpl_key 是冻结字段、必填（契约 §notify.ack）。这里**不兜底**：
        // 以前空 key 会静默记到 CHECKIN 上，症状是"补了三种、只有到点提醒的次数涨"——
        // 一个本该暴露的参数错误变成了一次看似正常的额度变更，排查要重走整条链路。
        // 宁可 1005（前端 .catch 里静默吞掉，用户无感），也不要写进错误的池子。
        var key = (req.TmplKey ?? string.Empty).Trim().ToUpperInvariant();
        if (key is not ("CHECKIN" or "MISSED" or "WEEKLY"))
            throw new ValidationException("tmpl_key 应为 CHECKIN / MISSED / WEEKLY");

        var accepted = req.Accepted?.Any(a => string.Equals((a ?? string.Empty).Trim(), key, StringComparison.OrdinalIgnoreCase)) == true;

        var row = await db.Subscriptions.FirstOrDefaultAsync(s => s.Openid == openid && s.TmplKey == key, ct);
        if (row is null)
        {
            row = new Subscription { Openid = openid, TmplKey = key, Quota = 0, MainSwitch = true };
            db.Add(row);
        }

        if (accepted)
        {
            row.Quota += 1;
            row.GrantedTotal += 1;
            row.LastGrantAt = now;
        }
        row.MainSwitch = req.Requested > 0 ? accepted || row.Quota > 0 : row.MainSwitch;

        await db.SaveChangesAsync(ct);

        var rows = await db.Subscriptions.Where(s => s.Openid == openid).ToListAsync(ct);
        int Get(string k) => rows.FirstOrDefault(r => r.TmplKey == k)?.Quota ?? 0;
        return new { quota = new QuotaDto { Checkin = Get("CHECKIN"), Missed = Get("MISSED"), Weekly = Get("WEEKLY") } };
    }
}

/// <summary>
/// report.batch：埋点攒批（前端 10 条 / 5 秒一次）。
/// 落 `stats_daily`，key = `evt_{name}`，value = `{count, dims}`。
///
/// 隐私是这里的第一约束（契约 §二.10）：参数**绝不落原文**，只留白名单 key 的枚举值分布。
/// 药名/疾病名/剂量文本都含非 ASCII 字符，`PrivacyFilter.IsSafeValue` 一条就挡住了绝大多数泄漏；
/// 白名单再挡一层"key 本身就叫 medicine_name"的情况。两道都过不了的字段静默丢弃 ——
/// 埋点因为一个脏字段整批报错，前端只会无限重投，脏数据还是进不来，指标反而全丢了。
/// </summary>
public sealed class ReportBatchHandler(IAppDbContext db, IClock clock)
{
    /// <summary>每个维度最多保留几个取值（长尾值直接不要，避免 jsonb 被无界枚举撑爆）。</summary>
    private const int MaxDimValues = 12;

    public async Task<object> HandleAsync(List<ReportEvent>? events, CancellationToken ct = default)
    {
        var batch = (events ?? []).Take(PrivacyFilter.MaxEvents).ToList();
        if (batch.Count == 0) return new { ok = true };

        var today = clock.TodayCn;
        var now = clock.NowMs;

        // 先按事件名聚合成一条增量（同一批里 10 次 checkin_success 只写一次库）
        var counters = new Dictionary<string, MetricCounter>(StringComparer.Ordinal);
        foreach (var e in batch)
        {
            if (!PrivacyFilter.IsSafeMetricKey(e.Name)) continue;

            var key = "evt_" + e.Name;
            if (!counters.TryGetValue(key, out var acc))
            {
                acc = new MetricCounter();
                counters[key] = acc;
            }
            acc.Count += 1;

            foreach (var (k, v) in PrivacyFilter.Sanitize(Flatten(e.Params)))
            {
                if (!acc.Dims.TryGetValue(k, out var bucket))
                {
                    bucket = new Dictionary<string, long>(StringComparer.Ordinal);
                    acc.Dims[k] = bucket;
                }
                bucket[v] = bucket.GetValueOrDefault(v) + 1;
            }
        }

        if (counters.Count == 0) return new { ok = true };

        var keys = counters.Keys.ToList();
        var rows = await db.StatsDaily
            .Where(s => s.StatDate == today && keys.Contains(s.MetricKey))
            .ToListAsync(ct);

        foreach (var (key, acc) in counters)
        {
            var row = rows.FirstOrDefault(r => r.MetricKey == key);
            if (row is null)
            {
                row = new StatsDaily { StatDate = today, MetricKey = key, MetricValue = "{}", CreatedAt = now };
                db.Add(row);
            }
            row.MetricValue = Merge(row.MetricValue, acc);
        }

        await db.SaveChangesAsync(ct);
        return new { ok = true };
    }

    private sealed class MetricCounter
    {
        public long Count;
        public Dictionary<string, Dictionary<string, long>> Dims { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>把批内增量合并进已有的 metric_value（读旧 → 累加 → 写回）。</summary>
    private static string Merge(string? oldJson, MetricCounter add)
    {
        long count = 0;
        var dims = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(oldJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(oldJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("count", out var c) && c.TryGetInt64(out var cv)) count = cv;
                if (root.TryGetProperty("dims", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    foreach (var dim in d.EnumerateObject())
                    {
                        if (dim.Value.ValueKind != JsonValueKind.Object) continue;
                        var bucket = new Dictionary<string, long>(StringComparer.Ordinal);
                        foreach (var v in dim.Value.EnumerateObject())
                            if (v.Value.TryGetInt64(out var n)) bucket[v.Name] = n;
                        dims[dim.Name] = bucket;
                    }
                }
            }
            catch (JsonException)
            {
                // 旧格式或脏数据：从 0 起算，不因为一行脏数据丢掉整批埋点
                count = 0;
                dims.Clear();
            }
        }

        count += add.Count;
        foreach (var (dim, bucket) in add.Dims)
        {
            if (!dims.TryGetValue(dim, out var target))
            {
                target = new Dictionary<string, long>(StringComparer.Ordinal);
                dims[dim] = target;
            }
            foreach (var (v, n) in bucket) target[v] = target.GetValueOrDefault(v) + n;
        }

        var payload = new Dictionary<string, object?>
        {
            ["count"] = count,
            ["dims"] = dims.ToDictionary(
                d => d.Key,
                d => (object?)d.Value
                    .OrderByDescending(x => x.Value)
                    .Take(MaxDimValues)
                    .ToDictionary(x => x.Key, x => x.Value)),
        };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>JsonElement 摊平成字符串，交给 PrivacyFilter 判安全（它只认字符串）。</summary>
    private static IEnumerable<KeyValuePair<string, string?>> Flatten(Dictionary<string, JsonElement>? raw)
    {
        if (raw is null) yield break;
        foreach (var (k, el) in raw)
        {
            var v = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => null,
            };
            if (v is not null) yield return new KeyValuePair<string, string?>(k, v);
        }
    }
}
