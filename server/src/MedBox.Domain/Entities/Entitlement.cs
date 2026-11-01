namespace MedBox.Domain.Entities;

/// <summary>激励视频换来的权益（无现金支付）。product_id = MED_SLOT / WEEK_REPORT / HISTORY_FOREVER。</summary>
public class Entitlement
{
    public string EntitlementId { get; set; } = "";
    public string FamilyId { get; set; } = "";
    public string Openid { get; set; } = "";
    public string ProductId { get; set; } = "";
    public int Credits { get; set; }
    public string Source { get; set; } = "ad";
    public long? ExpireAt { get; set; }
    public long CreatedAt { get; set; }
}
