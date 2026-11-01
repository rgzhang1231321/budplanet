namespace MedBox.Domain.Entities;

/// <summary>ad.unlock 防重放：ad_trace 唯一键 + 30s 窗 + 每场景日上限。</summary>
public class AdEvent
{
    public string AdTrace { get; set; } = "";
    public string Openid { get; set; } = "";
    public string FamilyId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public bool Granted { get; set; }
    public long CreatedAt { get; set; }
}
