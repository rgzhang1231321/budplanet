namespace MedBox.Domain.Entities;

public class Medicine
{
    public string MedicineId { get; set; } = "";
    public string FamilyId { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Spec { get; set; }
    /// <summary>展示串，后端不解析数值、不做 0.5→"半片" 转换。</summary>
    public string? DefaultDose { get; set; }
    public decimal? DoseAmount { get; set; }
    public string? DoseUnit { get; set; }
    public string? Remark { get; set; }
    public string? PhotoFileId { get; set; }
    public string? DrugCode { get; set; }
    public string Status { get; set; } = "active";
    public string? CreatedBy { get; set; }
    public long CreatedAt { get; set; }
}
