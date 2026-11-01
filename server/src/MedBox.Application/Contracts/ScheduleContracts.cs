using System.Text.Json.Serialization;

namespace MedBox.Application.Contracts;

/// <summary>schedule.today 的 rows[] 单条（契约冻结表 §6，逐字冻结）。</summary>
public sealed class DoseRowDto
{
    [JsonPropertyName("dose_id")] public string DoseId { get; set; } = "";
    [JsonPropertyName("pid")] public string Pid { get; set; } = "";
    /// <summary>当日分钟数 0–1439，不是"第几个时间点"的序号。</summary>
    [JsonPropertyName("slot")] public int Slot { get; set; }
    [JsonPropertyName("medicine_name")] public string MedicineName { get; set; } = "";
    /// <summary>该 slot 展开后的剂量串；前端禁做中文映射，也不得读药品默认剂量。</summary>
    [JsonPropertyName("dose_text")] public string DoseText { get; set; } = "";
    [JsonPropertyName("due_time")] public string DueTime { get; set; } = "";
    [JsonPropertyName("due_ts")] public long DueTs { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("is_late")] public bool IsLate { get; set; }
    [JsonPropertyName("taken_at")] public long TakenAt { get; set; }
    [JsonPropertyName("is_corrected")] public bool IsCorrected { get; set; }
    [JsonPropertyName("snooze_count")] public int SnoozeCount { get; set; }
    [JsonPropertyName("today_index")] public int TodayIndex { get; set; }
    [JsonPropertyName("today_total")] public int TodayTotal { get; set; }
}

public sealed class RecentViewDto
{
    [JsonPropertyName("alias")] public string Alias { get; set; } = "";
    [JsonPropertyName("at")] public long At { get; set; }
}

public sealed class TodayResultDto
{
    [JsonPropertyName("rows")] public List<DoseRowDto> Rows { get; set; } = [];
    [JsonPropertyName("recentView")] public RecentViewDto? RecentView { get; set; }
    [JsonPropertyName("server_ts")] public long ServerTs { get; set; }
}

public sealed class MissItemDto
{
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("slot")] public int Slot { get; set; }
    [JsonPropertyName("pid")] public string Pid { get; set; } = "";
    [JsonPropertyName("patient")] public string Patient { get; set; } = "";
    [JsonPropertyName("medicine_name")] public string MedicineName { get; set; } = "";
    [JsonPropertyName("dose_text")] public string DoseText { get; set; } = "";
}

public sealed class WeekResultDto
{
    /// <summary>完成率：只算"系统判定 + 自主打卡"，人工修改不进分子分母。</summary>
    [JsonPropertyName("rate")] public int Rate { get; set; }
    [JsonPropertyName("familyCount")] public int FamilyCount { get; set; }
    [JsonPropertyName("manualCorrectCount")] public int ManualCorrectCount { get; set; }
    [JsonPropertyName("missList")] public List<MissItemDto> MissList { get; set; } = [];
    [JsonPropertyName("server_ts")] public long ServerTs { get; set; }
}
