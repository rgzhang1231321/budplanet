using System.Text.Json.Serialization;

namespace MedBox.Application.Contracts;

public sealed class SlotDto
{
    [JsonPropertyName("t")] public string T { get; set; } = "";
    [JsonPropertyName("d")] public string D { get; set; } = "";
    [JsonPropertyName("slot_key")] public string SlotKey { get; set; } = "";
}

public sealed class MedicineItemDto
{
    [JsonPropertyName("mid")] public string Mid { get; set; } = "";
    [JsonPropertyName("uid")] public string Uid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("spec")] public string Spec { get; set; } = "";
    [JsonPropertyName("default_dose")] public string DefaultDose { get; set; } = "";
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("active")] public int Active { get; set; } = 1;
    [JsonPropertyName("slots")] public List<SlotDto> Slots { get; set; } = [];
    [JsonPropertyName("cycle_type")] public string CycleType { get; set; } = "long_term";
}

public sealed class MedicineListResult
{
    [JsonPropertyName("items")] public List<MedicineItemDto> Items { get; set; } = [];
    [JsonPropertyName("server_ts")] public long ServerTs { get; set; }
}

public sealed class CommonMedDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("spec")] public string? Spec { get; set; }
    [JsonPropertyName("default_dose")] public string? DefaultDose { get; set; }
}

public sealed class CommonMedResult
{
    [JsonPropertyName("items")] public List<CommonMedDto> Items { get; set; } = [];
}

public sealed class PlanUpsertRequest
{
    [JsonPropertyName("mid")] public string Mid { get; set; } = "";
    [JsonPropertyName("slots")] public List<SlotDto> Slots { get; set; } = [];
    [JsonPropertyName("cycle_type")] public string CycleType { get; set; } = "long_term";
    [JsonPropertyName("cycleDays")] public int? CycleDays { get; set; }
    [JsonPropertyName("startDate")] public string? StartDate { get; set; }
}

public sealed class PlanUpsertResult
{
    [JsonPropertyName("planId")] public string PlanId { get; set; } = "";
    [JsonPropertyName("nextFireAt")] public long NextFireAt { get; set; }
}
