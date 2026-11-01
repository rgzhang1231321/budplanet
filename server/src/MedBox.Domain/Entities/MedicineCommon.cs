namespace MedBox.Domain.Entities;

/// <summary>常用药预设，公开只读（GET /api/medicine/common）。</summary>
public class MedicineCommon
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Spec { get; set; }
    public string? DefaultDose { get; set; }
    public decimal? DoseAmount { get; set; }
    public string? DoseUnit { get; set; }
    public string[] Tags { get; set; } = [];
    public long CreatedAt { get; set; }
}
