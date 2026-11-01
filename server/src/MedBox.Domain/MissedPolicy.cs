namespace MedBox.Domain;

/// <summary>
/// 漏服判定。关键口径（契约 §三 missedThresholdMin）：
/// 判定基准是 **原定 slot**，不是 due_ts —— snooze 会把 due_ts 往后推，
/// 若跟着 due_ts 走，老人点一次"稍后提醒"就能把漏服判定也推后，漏服就永远判不出来。
/// </summary>
public static class MissedPolicy
{
    /// <summary>该剂量行"算漏服"的时刻 = 当日 slot 时间 + 阈值（默认 60 分钟）。</summary>
    public static long MissedAtUtcMs(string dueDate, int slot, int thresholdMin)
        => TimeCn.SlotUtcMs(dueDate, slot) + thresholdMin * 60_000L;

    /// <summary>只判 pending：已服/已跳过/已人工改判的一律不动。</summary>
    public static bool ShouldBeMissed(string status, bool isCorrected, string dueDate, int slot, long nowMs, int thresholdMin)
    {
        if (status != DoseStatusWire.Pending) return false;
        if (isCorrected) return false;
        return nowMs >= MissedAtUtcMs(dueDate, slot, thresholdMin);
    }
}

/// <summary>每日定时任务的"下一次触发时刻"计算（东八区 wall clock）。</summary>
public static class DailySchedule
{
    /// <summary>下一次东八区 hh:mm 的 UTC 毫秒；今天已过则取明天。</summary>
    public static long NextRunUtcMs(long nowMs, int hour, int minute)
    {
        var today = TimeCn.DateCn(nowMs);
        var todayRun = TimeCn.DayStartUtcMs(today) + (hour * 60 + minute) * 60_000L;
        if (todayRun > nowMs) return todayRun;
        return TimeCn.DayStartUtcMs(today) + 24 * 3600_000L + (hour * 60 + minute) * 60_000L;
    }
}
