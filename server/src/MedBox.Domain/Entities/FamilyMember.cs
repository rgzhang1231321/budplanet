namespace MedBox.Domain.Entities;

/// <summary>家庭成员。复合主键 (family_id, openid) 天然防重复加入。</summary>
public class FamilyMember
{
    public string FamilyId { get; set; } = "";
    public string Openid { get; set; } = "";
    /// <summary>admin | member | elder —— 只决定权限，不决定渲染。</summary>
    public string Role { get; set; } = "member";
    public string? Relation { get; set; }
    public bool IsPatient { get; set; }
    public string? SelfPatientId { get; set; }
    public bool NotifyEnabled { get; set; } = true;
    public long JoinedAt { get; set; }
}
