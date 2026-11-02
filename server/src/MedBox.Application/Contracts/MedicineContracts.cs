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
    /// <summary>与同表的 is_late / is_corrected 同为布尔；list 已按 status=active 过滤，恒为 true。</summary>
    [JsonPropertyName("active")] public bool Active { get; set; } = true;
    [JsonPropertyName("slots")] public List<SlotDto> Slots { get; set; } = [];
    [JsonPropertyName("cycle_type")] public string CycleType { get; set; } = "long_term";
    /// <summary>v1.6：计划状态 active / paused（finished 的药已不在此列表）。</summary>
    [JsonPropertyName("plan_status")] public string PlanStatus { get; set; } = "active";
    /// <summary>v1.6：暂停到期日（东八区 YYYY-MM-DD），仅 paused 时有值。</summary>
    [JsonPropertyName("pause_until")] public string? PauseUntil { get; set; }
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
    [JsonPropertyName("server_ts")] public long ServerTs { get; set; }
}

/// <summary>
/// medicine.suggest 的一项。出参形状刻意与 <see cref="CommonMedDto"/> 兼容（name/spec/default_dose 同名同义），
/// 多出的 <c>count</c> 是"这个药名在这个家庭被添加过几次"：0 = 平台预设补位，≥1 = 自己用过的。
/// </summary>
public sealed class MedicineSuggestDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("spec")] public string? Spec { get; set; }
    [JsonPropertyName("default_dose")] public string? DefaultDose { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class MedicineSuggestResult
{
    [JsonPropertyName("items")] public List<MedicineSuggestDto> Items { get; set; } = [];
    [JsonPropertyName("server_ts")] public long ServerTs { get; set; }
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

/// <summary>plan.pause（v1.6）：until = 东八区 YYYY-MM-DD，到期自动恢复。</summary>
public sealed class PlanPauseRequest
{
    [JsonPropertyName("mid")] public string Mid { get; set; } = "";
    [JsonPropertyName("until")] public string Until { get; set; } = "";
}

/// <summary>plan.resume（v1.6）：立即恢复。</summary>
public sealed class PlanResumeRequest
{
    [JsonPropertyName("mid")] public string Mid { get; set; } = "";
}
