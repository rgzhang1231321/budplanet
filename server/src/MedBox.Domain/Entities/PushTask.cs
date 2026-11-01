namespace MedBox.Domain.Entities;

/// <summary>推送任务队列（落库削峰）。task_id = md5(openid + 分钟桶 + tmpl_key) 保证幂等。</summary>
public class PushTask
{
    public string TaskId { get; set; } = "";
    public short Shard { get; set; }
    public string Openid { get; set; } = "";
    public string TmplKey { get; set; } = "";
    /// <summary>queued / sending / sent / dropped / failed</summary>
    public string Status { get; set; } = "queued";
    public short Priority { get; set; }
    public string Payload { get; set; } = "{}";
    public short RetryCount { get; set; }
    public long NextRetryAt { get; set; }
    public int? Errcode { get; set; }
    public long CreatedAt { get; set; }
    public long? SentAt { get; set; }
}
