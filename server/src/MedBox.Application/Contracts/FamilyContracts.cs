using System.Text.Json.Serialization;

namespace MedBox.Application.Contracts;

public sealed class FamilyInfoDto
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "member";
    /// <summary>只决定渲染（elder/guardian）；服务端派生，不落库。</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "guardian";
    [JsonPropertyName("selfUid")] public string? SelfUid { get; set; }
    [JsonPropertyName("selfUids")] public string[] SelfUids { get; set; } = [];
    [JsonPropertyName("members")] public List<MemberDto> Members { get; set; } = [];
    [JsonPropertyName("patients")] public List<PatientDto> Patients { get; set; } = [];
    /// <summary>
    /// 只有 admin 拿得到；其余角色为 null。邀请令牌等于"能把外人拉进这个家"的凭证，
    /// 下发到 elder 客服端就等于给了老人一个绕过 family.reset_invite 权限的后门。
    /// </summary>
    [JsonPropertyName("invite_token")] public string? InviteToken { get; set; }
    [JsonPropertyName("server_ts")] public long ServerTs { get; set; }
}

public sealed class MemberDto
{
    [JsonPropertyName("openid")] public string Openid { get; set; } = "";
    [JsonPropertyName("nick")] public string Nick { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "member";
}

public sealed class PatientDto
{
    [JsonPropertyName("uid")] public string Uid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("alias")] public string Alias { get; set; } = "";
    [JsonPropertyName("avatar")] public string? Avatar { get; set; }
    [JsonPropertyName("selfOpenid")] public string? SelfOpenid { get; set; }
    /// <summary>默认界面 normal / large（v1.7）。子女可远程设，老人本机手动切换优先。</summary>
    [JsonPropertyName("display_mode")] public string DisplayMode { get; set; } = "normal";
}

public sealed class FamilyListResult
{
    [JsonPropertyName("families")] public List<FamilyListItemDto> Families { get; set; } = [];
    [JsonPropertyName("server_ts")] public long ServerTs { get; set; }
}
