namespace MedBox.Domain.Entities;

/// <summary>服药人。self_openid 是 mode=elder 的唯一判据（per-family 现算，不落 user）。</summary>
public class Patient
{
    public string PatientId { get; set; } = "";
    public string FamilyId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Alias { get; set; }
    public string? AvatarFileId { get; set; }
    public string? SelfOpenid { get; set; }
    public string[] ChronicTags { get; set; } = [];
    public string Status { get; set; } = "active";
    public long CreatedAt { get; set; }
}
