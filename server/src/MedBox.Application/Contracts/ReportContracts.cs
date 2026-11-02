using System.Text.Json;
using System.Text.Json.Serialization;

namespace MedBox.Application.Contracts;

/// <summary>report.batch 入参（契约 §二.10）。params 不落原文，只留白名单枚举分布。</summary>
public sealed class ReportEvent
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("params")] public Dictionary<string, JsonElement>? Params { get; set; }
}

public sealed class ReportBatchRequest
{
    [JsonPropertyName("events")] public List<ReportEvent> Events { get; set; } = [];
}

/// <summary>report.export 入参（v1.6）：days 默认 30，范围 1–90。</summary>
public sealed class ReportExportRequest
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    [JsonPropertyName("days")] public int? Days { get; set; }
}

public sealed class ReportRangeDto
{
    [JsonPropertyName("start")] public string Start { get; set; } = "";
    [JsonPropertyName("end")] public string End { get; set; } = "";
}

public sealed class ReportMedDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("usage")] public string Usage { get; set; } = "";
    /// <summary>用户记录的完成率（非依从性评估）；排除人工改判行。</summary>
    [JsonPropertyName("rate")] public int Rate { get; set; }
}

/// <summary>report.export 出参（v1.6）。不含诊断/建议/医嘱，仅供复诊说明服药情况。</summary>
public sealed class ReportExportResult
{
    [JsonPropertyName("range")] public ReportRangeDto Range { get; set; } = new();
    [JsonPropertyName("rate")] public int Rate { get; set; }
    [JsonPropertyName("missCount")] public int MissCount { get; set; }
    [JsonPropertyName("medCount")] public int MedCount { get; set; }
    [JsonPropertyName("meds")] public List<ReportMedDto> Meds { get; set; } = [];
    [JsonPropertyName("server_ts")] public long ServerTs { get; set; }
}
