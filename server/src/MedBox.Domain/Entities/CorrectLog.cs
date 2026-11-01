namespace MedBox.Domain.Entities;

/// <summary>人工改判审计（checkin.correct 专用，可用药史唯一凭证）。</summary>
public class CorrectLog
{
    public string LogId { get; set; } = "";
    public string DoseId { get; set; } = "";
    public string FamilyId { get; set; } = "";
    public string OperatorOpenid { get; set; } = "";
    public string FromStatus { get; set; } = "";
    public string ToStatus { get; set; } = "";
    public string? Reason { get; set; }
    public long CreatedAt { get; set; }
}
