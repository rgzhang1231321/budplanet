using System.Text.Json.Serialization;

namespace MedBox.Application.Contracts;

public sealed class LoginRequest
{
    /// <summary>wx.login 拿到的 code；自建后端必须 code2session，前端永不传 openid。</summary>
    [JsonPropertyName("code")] public string Code { get; set; } = "";
}

/// <summary>user.login 出参（契约冻结表 §二.1）。字段名逐字冻结。</summary>
public sealed class LoginResultDto
{
    [JsonPropertyName("openid")] public string Openid { get; set; } = "";
    [JsonPropertyName("user")] public UserDto User { get; set; } = new();
    [JsonPropertyName("families")] public List<FamilyListItemDto> Families { get; set; } = [];
    [JsonPropertyName("entitlement")] public EntitlementDto Entitlement { get; set; } = new();
    [JsonPropertyName("config")] public AppConfigDto Config { get; set; } = new();
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";
}

public sealed class UserDto
{
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
    [JsonPropertyName("avatar")] public string Avatar { get; set; } = "";
    [JsonPropertyName("disclaimer_ack_at")] public long DisclaimerAckAt { get; set; }
    [JsonPropertyName("privacy_ack_at")] public long PrivacyAckAt { get; set; }
    [JsonPropertyName("setting")] public UserSettingDto Setting { get; set; } = new();
}

public sealed class UserSettingDto
{
    [JsonPropertyName("sound")] public bool Sound { get; set; } = true;
    [JsonPropertyName("quiet")] public bool Quiet { get; set; } = true;
    [JsonPropertyName("reduceMotion")] public bool ReduceMotion { get; set; }
}

public sealed class EntitlementDto
{
    [JsonPropertyName("memberLevel")] public string MemberLevel { get; set; } = "free";
    [JsonPropertyName("credits")] public int Credits { get; set; }
}

public sealed class FamilyListItemDto
{
    [JsonPropertyName("fid")] public string Fid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "member";
}

/// <summary>config 全量字段（契约冻结表 §三）。整块 camelCase，键名不得改。</summary>
public sealed class AppConfigDto
{
    [JsonPropertyName("adOn")] public bool AdOn { get; set; }
    [JsonPropertyName("adSwitch")] public AdSwitchDto AdSwitch { get; set; } = new();
    [JsonPropertyName("interstitialCool")] public int InterstitialCool { get; set; } = 30;
    [JsonPropertyName("interstitialDayMax")] public int InterstitialDayMax { get; set; } = 3;
    [JsonPropertyName("freeMedLimit")] public int FreeMedLimit { get; set; } = 3;
    [JsonPropertyName("freePatientLimit")] public int FreePatientLimit { get; set; } = 1;
    [JsonPropertyName("tmplIds")] public TmplIdsDto TmplIds { get; set; } = new();
    [JsonPropertyName("quietHours")] public QuietHoursDto QuietHours { get; set; } = new();
    [JsonPropertyName("missedThresholdMin")] public int MissedThresholdMin { get; set; } = 60;
    [JsonPropertyName("snoozeDeadlineMin")] public int SnoozeDeadlineMin { get; set; } = 45;
    [JsonPropertyName("notice")] public string Notice { get; set; } = "";
}

public sealed class AdSwitchDto
{
    [JsonPropertyName("interstitial")] public bool Interstitial { get; set; } = true;
    [JsonPropertyName("elderInterstitial")] public bool ElderInterstitial { get; set; }
}

public sealed class TmplIdsDto
{
    [JsonPropertyName("CHECKIN")] public string Checkin { get; set; } = "";
    [JsonPropertyName("MISSED")] public string Missed { get; set; } = "";
    [JsonPropertyName("WEEKLY")] public string Weekly { get; set; } = "";
}

public sealed class QuietHoursDto
{
    [JsonPropertyName("start")] public string Start { get; set; } = "22:00";
    [JsonPropertyName("end")] public string End { get; set; } = "07:00";
}
