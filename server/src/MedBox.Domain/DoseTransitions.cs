namespace MedBox.Domain;

/// <summary>
/// 状态迁移规则（契约冻结表 §七 v1.5 口径）。
/// 分两条通道，别合并：
///   · Submit（打卡 / 补记）：pending → taken|skipped|missed；missed(未人工改判) → taken
///   · Correct（人工改判）：taken → missed|skipped，目标状态只有这两项
/// 契约 §7:149 澄清：`missed(系统判定) → taken` 走 submit，不走 correct ——
/// correct 是"记错了要改"，submit 是"我吃了"；合并会让 correct 能凭空造出打卡时间。
/// 人工改判不可逆：is_corrected=true 之后任何方向都锁死（防来回刷数据）。
/// </summary>
public static class DoseTransitions
{
    /// <summary>打卡通道：老人自主打卡 / 补记。</summary>
    public static bool CanSubmit(DoseStatus from, DoseStatus to, bool isCorrected)
    {
        if (from == to) return true; // 幂等空操作，由调用方标 changed=false

        return from switch
        {
            DoseStatus.Pending => to is DoseStatus.Taken or DoseStatus.Skipped or DoseStatus.Missed,
            // 系统判的漏服可以救（吃了没打卡是这个产品最高频的假阳性）
            DoseStatus.Missed => !isCorrected && to == DoseStatus.Taken,
            _ => false,
        };
    }

    /// <summary>人工改判通道：管理员在看板里改状态。</summary>
    public static bool CanCorrect(DoseStatus from, DoseStatus to, bool isCorrected)
    {
        if (isCorrected) return false; // 已改判过，任何方向都不许再动
        if (from == to) return false;

        return from == DoseStatus.Taken && IsCorrectTarget(to);
    }

    /// <summary>correct 的合法目标状态：只收 missed / skipped，不含 taken（回 taken 走 submit）。</summary>
    public static bool IsCorrectTarget(DoseStatus to) => to is DoseStatus.Missed or DoseStatus.Skipped;
}
