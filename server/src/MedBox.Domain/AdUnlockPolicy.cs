namespace MedBox.Domain;

/// <summary>
/// ad.unlock 的防重放与频控规则（纯函数，可单测）。
/// 阈值一律来自 config 下发的 interstitialCool / interstitialDayMax ——
/// 后端再写一套 30s / 5 次就是另立口径，运营改配置反而不生效。
/// </summary>
public static class AdUnlockPolicy
{
    /// <summary>冷却窗内重复召唤同一个场景：视为刷广告。窗口 &lt;= 0 表示不限制。</summary>
    public static bool InCooldown(long nowMs, long? lastGrantAtMs, int coolSeconds)
    {
        if (lastGrantAtMs is null || coolSeconds <= 0) return false;
        return nowMs - lastGrantAtMs.Value < coolSeconds * 1000L;
    }

    /// <summary>当日该场景已核销次数是否已达上限。上限 &lt;= 0 表示不限。</summary>
    public static bool ExceedsDayMax(int todayCount, int dayMax) => dayMax > 0 && todayCount >= dayMax;
}
