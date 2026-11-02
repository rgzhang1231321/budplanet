namespace MedBox.Domain.Entities;

/// <summary>用药计划。1 药品 : 1 计划；slots 存 JSONB，times_minute 派生双写供扫描。</summary>
public class Plan
{
    public string PlanId { get; set; } = "";
    public string FamilyId { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string MedicineId { get; set; } = "";
    /// <summary>JSONB：[{t:"08:00",d:"1片",slot_key:"s0800"}]</summary>
    public string Slots { get; set; } = "[]";
    /// <summary>派生列，以它为准（禁止拿 JSONB 当查询条件）。</summary>
    public int[] TimesMinute { get; set; } = [];
    public string CycleType { get; set; } = "long_term";
    public int? CycleDays { get; set; }
    public string? CycleEndDate { get; set; }
    public string StartDate { get; set; } = "";
    public string Status { get; set; } = "active";
    /// <summary>暂停到期日（东八区 YYYY-MM-DD）。status='paused' 时有效，到期后物化时自动恢复 active。</summary>
    public string? PauseUntil { get; set; }
    public int AdvanceMin { get; set; }
    public string? CreatedBy { get; set; }
    public long UpdatedAt { get; set; }
}
