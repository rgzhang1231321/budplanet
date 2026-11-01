namespace MedBox.Domain;

/// <summary>
/// 每周定时任务的"今天到点了吗"判定（东八区 wall clock）。
/// 抽成纯函数是为了能单测：写在 Worker 的循环里就只能靠改系统时间或等一周才验证一次。
/// </summary>
public static class WeeklySchedule
{
    /// <summary>东八区今天是 <paramref name="day"/> 且已过 <paramref name="hour"/> 点 → 到点。</summary>
    public static bool IsDue(long nowMs, DayOfWeek day, int hour)
    {
        var now = TimeCn.ToCn(nowMs);
        return now.DayOfWeek == day && now.Hour >= hour;
    }
}
