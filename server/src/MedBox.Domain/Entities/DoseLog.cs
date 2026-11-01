namespace MedBox.Domain.Entities;

/// <summary>剂量行（核心表，量最大）。dose_id = ${pid}_${date}_${slot}。</summary>
public class DoseLog
{
    public string DoseId { get; set; } = "";
    public string FamilyId { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string PlanId { get; set; } = "";
    public string MedicineId { get; set; } = "";
    /// <summary>该 slot 展开后的剂量快照。打卡卡片只渲染它，不读药品默认剂量。</summary>
    public string? DoseText { get; set; }
    /// <summary>药名快照：改名/删 slot 不影响历史。</summary>
    public string? MedNameSnapshot { get; set; }
    public string DueDate { get; set; } = "";
    /// <summary>= due_minute，0–1439。</summary>
    public int Slot { get; set; }
    public long DueTs { get; set; }
    public string Status { get; set; } = "pending";
    public long? TakenAt { get; set; }
    public string? TakenBy { get; set; }
    public bool IsLate { get; set; }
    public string? SkipReason { get; set; }
    public int SnoozeCount { get; set; }
    public short RemindStatus { get; set; }
    public long? RemindAt { get; set; }
    public int? RemindErrcode { get; set; }
    public short AlertStatus { get; set; }
    public long? AlertAt { get; set; }
    /// <summary>
    /// 东八区 ISO 周键（"2026-W44"），由 <see cref="DueDate"/> 同源推导，物化时写入
    /// （<see cref="MedBox.Domain.TimeCn.WeekKey"/>）。ix_dose_logs_week (patient_id, week_key) 靠它命中。
    /// 注意：本批之前生成的历史行该列为 NULL，按 week_key 查询时要兼容空值。
    /// </summary>
    public string? WeekKey { get; set; }
    /// <summary>人工改判标记：true 之后任何方向都锁死，且不进完成率。</summary>
    public bool IsCorrected { get; set; }
    public string? CheckedBy { get; set; }
    public long? CheckedAt { get; set; }
    public long CreatedAt { get; set; }
}
