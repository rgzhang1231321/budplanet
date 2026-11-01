namespace MedBox.Domain.Entities;

public class Family
{
    public string FamilyId { get; set; } = "";
    public string Name { get; set; } = "";
    public string OwnerOpenid { get; set; } = "";
    public string? InviteToken { get; set; }
    public long? InviteExpireAt { get; set; }
    public string Tier { get; set; } = "free";
    public int PatientCount { get; set; }
    public int MemberCount { get; set; }
    public long? HistoryForeverAt { get; set; }
    public long CreatedAt { get; set; }
}
