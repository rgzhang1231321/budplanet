namespace MedBox.Domain.Entities;

/// <summary>
/// 全平台药名引用次数（V008）。按归一药名聚合，**不含任何家庭/服药人标识**：
/// 每次添加 +1、每次删除（归档）−1，下限 0。
/// </summary>
public class MedicineUsageStat
{
    /// <summary>归一键（lower + trim），跨家庭唯一，等于 <see cref="MedicineUsage.Normalize"/> 的结果。</summary>
    public string NameKey { get; set; } = "";

    /// <summary>展示名：最近一次添加时的写法。</summary>
    public string Name { get; set; } = "";
    public string? Spec { get; set; }
    public string? DefaultDose { get; set; }
    public int UseCount { get; set; }
    public long LastUsedAt { get; set; }
}
