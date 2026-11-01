namespace MedBox.Domain.Entities;

/// <summary>增长看板日指标（聚合值，不含可识别个人的明细）。</summary>
public class StatsDaily
{
    public string StatDate { get; set; } = "";
    public string MetricKey { get; set; } = "";
    public string MetricValue { get; set; } = "{}";
    public long CreatedAt { get; set; }
}
