namespace MedBox.Domain.Entities;

/// <summary>周报归档后的聚合（明细被 ArchiveWorker 清掉后，这里仍能出周报）。</summary>
public class WeeklyStat
{
    public string FamilyId { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string WeekKey { get; set; } = "";
    public int Total { get; set; }
    public int TakenCnt { get; set; }
    public int MissedCnt { get; set; }
    public int SkippedCnt { get; set; }
    /// <summary>人工改判次数，单列、不计入完成率。</summary>
    public int ManualCorrectCount { get; set; }
    public long UpdatedAt { get; set; }
}
