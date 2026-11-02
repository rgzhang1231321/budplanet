using System.Text.Json.Serialization;

namespace MedBox.Api.Contracts;

/// <summary>只放"传参外壳"，业务语义的 DTO 在 Application.Contracts。</summary>
public sealed class FamilyCreateRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "我家";
}

public sealed class FamilyJoinRequest
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    [JsonPropertyName("ivt")] public string Ivt { get; set; } = "";
    /// <summary>patient = 我自己吃药（入参意图，入库恒为 admin|member|elder）。</summary>
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("uid")] public string? Uid { get; set; }
}

public sealed class PatientCreateRequest
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("alias")] public string? Alias { get; set; }
    [JsonPropertyName("avatar")] public string? Avatar { get; set; }
}

public sealed class MedicineCreateRequest
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    [JsonPropertyName("uid")] public string Uid { get; set; } = "";
    [JsonPropertyName("mid")] public string? Mid { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("spec")] public string? Spec { get; set; }
    [JsonPropertyName("default_dose")] public string? DefaultDose { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

public sealed class PatientUpdateRequest
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    [JsonPropertyName("uid")] public string Uid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("alias")] public string? Alias { get; set; }
    [JsonPropertyName("avatar")] public string? Avatar { get; set; }
}

/// <summary>patient.set_mode（v1.7）：子女给服药人设默认界面 normal/large。</summary>
public sealed class PatientSetModeRequest
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    [JsonPropertyName("uid")] public string Uid { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "normal";
}

public sealed class PatientArchiveRequest
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    [JsonPropertyName("uid")] public string Uid { get; set; } = "";
}

public sealed class MedicineArchiveRequest
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    [JsonPropertyName("mid")] public string Mid { get; set; } = "";
}

public sealed class FamilyUpdateRoleRequest
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    /// <summary>要调整的那位家人的 openid（来自 family.info 的 members[]）。</summary>
    [JsonPropertyName("openid")] public string Openid { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "";
}

/// <summary>只带 fid 的入参：family.leave / family.reset_invite 共用。</summary>
public sealed class FamilyFidRequest
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
}

/// <summary>family.set_member_name：管理员给家人起家内称呼（label 传空串 = 清掉，回落微信昵称）。</summary>
public sealed class FamilySetMemberNameRequest
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    [JsonPropertyName("openid")] public string Openid { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
}

/// <summary>family.bind_member：管理员手动绑定「家人 ↔ 服药档案」；uid 传空串 = 解绑。</summary>
public sealed class FamilyBindMemberRequest
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    [JsonPropertyName("openid")] public string Openid { get; set; } = "";
    [JsonPropertyName("uid")] public string? Uid { get; set; }
}

public sealed class PlanFinishRequest
{
    [JsonPropertyName("planId")] public string PlanId { get; set; } = "";
}

public sealed class UserEraseRequest
{
    /// <summary>必须是 true —— 注销不可逆，前端要用户明确二次确认才发。</summary>
    [JsonPropertyName("confirm")] public bool Confirm { get; set; }
}
