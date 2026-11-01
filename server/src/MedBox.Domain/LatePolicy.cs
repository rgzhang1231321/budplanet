namespace MedBox.Domain;

/// <summary>
/// 「迟到」判定（is_late 布尔，不是第五个状态）。
/// 基准是**原定 slot**，与漏服判定同源：snooze 会把 due_ts 往后推，
/// 若拿 due_ts 比，老人点一次「稍后提醒」再打卡就永远不算迟到，配色当场失真。
/// </summary>
public static class LatePolicy
{
    /// <summary>打卡时刻是否已超过「原定 slot + 容差」。容差走配置（Checkin:LateToleranceMin）。</summary>
    public static bool IsLate(string dueDate, int slot, long takenAtMs, int toleranceMin)
        => takenAtMs - TimeCn.SlotUtcMs(dueDate, slot) > Math.Max(0, toleranceMin) * 60_000L;
}
