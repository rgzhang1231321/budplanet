namespace MedBox.Domain;

/// <summary>CAS 循环下一步该做什么。</summary>
public enum CasStep
{
    /// <summary>许可迁移且还有重试额度 → 打带状态守卫的 UPDATE。</summary>
    Apply,
    /// <summary>已经是目标状态 → 幂等空操作（changed=false，仍要出队）。</summary>
    AlreadyDone,
    /// <summary>状态不允许 / 重试额度用完 → rejected 1005。</summary>
    Reject,
}

/// <summary>
/// 打卡 CAS 的重试判定（纯函数，可单测）。
/// 为什么要 CAS：打卡是"读出来 → 改内存对象 → 存回去"，漏服判定 worker 同时在把 pending 改 missed，
/// 后写覆盖先写，用户明明点了「已服用」最终却是 missed —— 静默记错吃药记录。
/// 规则：单条最多两次 UPDATE（1 次正常 + 1 次冲突重试），第二次仍 0 行就判 rejected，
/// 绝不因为 0 行就返回成功（那就是假成功）。
/// </summary>
public static class DoseCasPolicy
{
    /// <summary>单条最多尝试的 UPDATE 次数，防并发下无限重试。</summary>
    public const int MaxAttempts = 2;

    public static CasStep Decide(DoseStatus from, DoseStatus to, bool isCorrected, int attemptsUsed)
    {
        if (from == to) return CasStep.AlreadyDone;
        if (attemptsUsed >= MaxAttempts) return CasStep.Reject;
        return DoseTransitions.CanSubmit(from, to, isCorrected) ? CasStep.Apply : CasStep.Reject;
    }
}
