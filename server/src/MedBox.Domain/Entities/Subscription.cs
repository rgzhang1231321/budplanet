namespace MedBox.Domain.Entities;

/// <summary>订阅消息额度池。工具-效率类目只能一次性订阅：授权 1 次 = 1 条额度。</summary>
public class Subscription
{
    public string Openid { get; set; } = "";
    public string TmplKey { get; set; } = "";
    public string? TmplId { get; set; }
    public int Quota { get; set; }
    public bool MainSwitch { get; set; } = true;
    public int GrantedTotal { get; set; }
    public int UsedTotal { get; set; }
    public long? LastGrantAt { get; set; }
    public long? LastUsedAt { get; set; }
    public long? ExhaustWarnedAt { get; set; }
}
