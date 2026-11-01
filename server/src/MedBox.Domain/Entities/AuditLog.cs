namespace MedBox.Domain.Entities;

/// <summary>越权（1002/1003）必须落这张表。</summary>
public class AuditLog
{
    public string LogId { get; set; } = "";
    public string? Openid { get; set; }
    public string? FamilyId { get; set; }
    public string Action { get; set; } = "";
    public int ResultCode { get; set; }
    public string Detail { get; set; } = "{}";
    public long CreatedAt { get; set; }
}
