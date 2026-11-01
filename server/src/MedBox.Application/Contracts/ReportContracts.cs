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
