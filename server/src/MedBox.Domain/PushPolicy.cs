using System.Security.Cryptography;
using System.Text;

namespace MedBox.Domain;

/// <summary>推送任务的判决结果。</summary>
public enum PushOutcome
{
    /// <summary>发成功，扣额度。</summary>
    Delivered,
    /// <summary>不可恢复，丢弃（拒收/未关注/openid 无效）。</summary>
    Drop,
    /// <summary>可重试（频率超限/网络抖动）。</summary>
    Retry,
    /// <summary>不该重试的失败（模板参数不合法、模板未配置）。</summary>
    Fail,
}

/// <summary>
/// 推送策略（纯函数，可单测）。
/// errcode 判决表来自微信订阅消息文档；错判的代价是老人收不到提醒，所以宁可 Fail 也不静默丢。
/// </summary>
public static class PushPolicy
{
    public const int ErrUserRefused = 43101;      // 用户拒收（一次性额度已用完或被拒）
    public const int ErrUserUnsubscribed = 43004;  // 用户未关注/未订阅
    public const int ErrInvalidOpenid = 40003;     // openid 无效
    public const int ErrApiLimit = 45009;          // 接口调用超过限额
    public const int ErrTemplateInvalid = 47003;   // 模板参数不合法（代码问题，重试无意义）
    public const int ErrTemplateMissing = -2;      // 本地：tmpl_id 未配置
    public const int ErrNoAccessToken = -3;        // 本地：拿不到 access_token

    public static PushOutcome Decide(int errCode) => errCode switch
    {
        0 => PushOutcome.Delivered,
        ErrUserRefused => PushOutcome.Drop,
        ErrUserUnsubscribed => PushOutcome.Drop,
        ErrInvalidOpenid => PushOutcome.Drop,
        ErrApiLimit => PushOutcome.Retry,
        ErrTemplateInvalid => PushOutcome.Fail,
        ErrTemplateMissing => PushOutcome.Fail,
        ErrNoAccessToken => PushOutcome.Retry,
        _ => PushOutcome.Retry, // 未知错误先重试，超过上限转 failed
    };

    /// <summary>指数退避：60s、120s、240s，封顶 10 分钟。</summary>
    public static long RetryDelayMs(int retryCount)
    {
        var n = Math.Clamp(retryCount, 0, 4);
        return Math.Min(60_000L * (1L << n), 600_000L);
    }

    public const int MaxRetry = 3;

    /// <summary>
    /// 分片数。改这个值会让**库里已有任务的 shard 与查询口径不一致**，
    /// 所以它是编译期常量而不是配置项（配置项会出现"入队用新值、消费用旧值"）：
    /// 真要改，必须同时回填 `UPDATE push_tasks SET shard = …`。
    /// </summary>
    public const int ShardCount = 4;

    /// <summary>
    /// 稳定分片：openid 的 FNV-1a 32 位哈希对分片数取模。
    /// **不能用 `string.GetHashCode()`** —— 它带进程级随机种子，重启后同一 openid 会换分片，
    /// 按 shard 过滤的消费组就会漏掉/重复处理老任务；`Math.Abs(int.MinValue)` 还会抛 OverflowException。
    /// 用无符号取模，天然非负。
    /// </summary>
    public static short Shard(string openid)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(openid))
        {
            hash ^= b;
            hash = unchecked(hash * 16777619u);
        }
        return (short)(hash % ShardCount);
    }

    /// <summary>任务幂等键：md5(openid + 分钟桶 + tmpl_key)，同一分钟内不重复入队。</summary>
    public static string TaskId(string openid, long minuteBucket, string tmplKey) =>
        Hash($"{openid}|{minuteBucket}|{tmplKey}");

    /// <summary>
    /// 按业务域幂等：md5(openid + #域 + tmpl_key)。周报用 week_key 作域，
    /// 这样 Worker 重启补跑时不会因为"分钟桶变了"再发一条上周周报。
    /// </summary>
    public static string ScopedTaskId(string openid, string scope, string tmplKey) =>
        Hash($"{openid}|#{scope}|{tmplKey}");

    private static string Hash(string raw) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}

/// <summary>免打扰窗口（默认 22:00–07:00 跨零点）。</summary>
public static class QuietHours
{
    public static bool Contains(string start, string end, int minuteOfDay)
    {
        if (!TimeCn.TryParseHhMm(start, out var s) || !TimeCn.TryParseHhMm(end, out var e)) return false;
        if (s == e) return false;
        // 跨零点：22:00–07:00 → minute >= 1320 || minute < 420
        return s < e ? minuteOfDay >= s && minuteOfDay < e : minuteOfDay >= s || minuteOfDay < e;
    }
}
