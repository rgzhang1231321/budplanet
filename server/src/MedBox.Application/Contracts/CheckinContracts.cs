using System.Text.Json.Serialization;

namespace MedBox.Application.Contracts;

/// <summary>checkin.submit 入参。items[] 传 pid/date/slot 三字段，禁止从 dose_id 解析反推。</summary>
public sealed class SubmitRequest
{
    [JsonPropertyName("items")] public List<SubmitItem> Items { get; set; } = [];
    [JsonPropertyName("idemKey")] public string? IdemKey { get; set; }
}

public sealed class SubmitItem
{
    [JsonPropertyName("pid")] public string Pid { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("slot")] public int Slot { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "taken";
    [JsonPropertyName("skip_reason")] public string? SkipReason { get; set; }
}

public sealed class AppliedItem
{
    [JsonPropertyName("pid")] public string Pid { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("slot")] public int Slot { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("ts")] public long Ts { get; set; }
    /// <summary>true=真正写入；false=幂等空操作。两种都出队。</summary>
    [JsonPropertyName("changed")] public bool Changed { get; set; }
}

public sealed class RejectedItem
{
    [JsonPropertyName("pid")] public string Pid { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("slot")] public int Slot { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string Msg { get; set; } = "";
}

public sealed class SubmitResultDto
{
    [JsonPropertyName("applied")] public List<AppliedItem> Applied { get; set; } = [];
    [JsonPropertyName("rejected")] public List<RejectedItem> Rejected { get; set; } = [];
    [JsonPropertyName("quota")] public QuotaDto? Quota { get; set; }
    [JsonPropertyName("server_ts")] public long ServerTs { get; set; }
}

public sealed class SnoozeRequest
{
    [JsonPropertyName("dose_id")] public string DoseId { get; set; } = "";
}

public sealed class SnoozeResultDto
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("due_ts")] public long DueTs { get; set; }
    [JsonPropertyName("snooze_count")] public int SnoozeCount { get; set; }
}

public sealed class CorrectRequest
{
    [JsonPropertyName("dose_id")] public string DoseId { get; set; } = "";
    [JsonPropertyName("to_status")] public string ToStatus { get; set; } = "";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class CorrectResultDto
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("is_corrected")] public bool IsCorrected { get; set; }
}

public sealed class QuotaDto
{
    [JsonPropertyName("CHECKIN")] public int Checkin { get; set; }
    [JsonPropertyName("MISSED")] public int Missed { get; set; }
    [JsonPropertyName("WEEKLY")] public int Weekly { get; set; }
}

public sealed class QuotaResultDto
{
    [JsonPropertyName("quota")] public QuotaDto Quota { get; set; } = new();
    [JsonPropertyName("mainSwitch")] public bool MainSwitch { get; set; } = true;
    [JsonPropertyName("server_ts")] public long ServerTs { get; set; }
}

public sealed class AckRequest
{
    [JsonPropertyName("tmpl_key")] public string TmplKey { get; set; } = "";
    [JsonPropertyName("accepted")] public string[] Accepted { get; set; } = [];
    [JsonPropertyName("requested")] public int Requested { get; set; }
}
