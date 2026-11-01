namespace MedBox.Domain.Entities;

/// <summary>用户。openid 直接做主键（微信身份是唯一信任根）。</summary>
public class User
{
    public string Openid { get; set; } = "";
    public string? Unionid { get; set; }
    public string? NickName { get; set; }
    public string? AvatarUrl { get; set; }
    public string[] FamilyIds { get; set; } = [];
    public string? ActiveFamilyId { get; set; }
    /// <summary>JSONB：{sound_on,dnd_start,dnd_end,font_scale,reduce_motion}</summary>
    public string Setting { get; set; } = "{}";
    /// <summary>JSONB：{ad_free_until,family_until,source}</summary>
    public string Entitlement { get; set; } = "{}";
    public long? PrivacyAgreedAt { get; set; }
    public long? DisclaimerAgreedAt { get; set; }
    public bool Unreachable { get; set; }
    public long CreatedAt { get; set; }
    public long? LastLoginAt { get; set; }
}
