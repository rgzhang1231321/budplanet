using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using MedBox.Application.Services;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.UseCases;

/// <summary>
/// user.login：code2session → upsert user → 返回 user/families/entitlement/config + JWT。
/// session_key 只存服务端，永不下发。
/// </summary>
public sealed class LoginHandler(IAppDbContext db, IWxApiClient wx, IJwtIssuer jwt, IClock clock, ConfigReader config, EntitlementService entitlements)
{
    public async Task<LoginResultDto> HandleAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new Domain.ValidationException("缺少 code");

        var session = await wx.Code2SessionAsync(code, ct);
        var openid = session.Openid;
        var now = clock.NowMs;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Openid == openid, ct);
        if (user is null)
        {
            user = new User { Openid = openid, CreatedAt = now, FamilyIds = [], Setting = "{}", Entitlement = "{}" };
            db.Add(user);
        }
        user.LastLoginAt = now;
        // 人回来了就说明这条通道可达：注销时置的 unreachable 必须在登录时清掉，
        // 否则注销后重新注册的账号永远收不到订阅消息，而界面上一切正常 —— 最难查的一类问题。
        user.Unreachable = false;
        if (!string.IsNullOrEmpty(session.Unionid)) user.Unionid = session.Unionid;
        await db.SaveChangesAsync(ct);

        var memberRows = await db.FamilyMembers.Where(m => m.Openid == openid).ToListAsync(ct);
        var fids = memberRows.Select(m => m.FamilyId).ToList();
        var families = await db.Families.Where(f => fids.Contains(f.FamilyId)).ToListAsync(ct);

        var cfg = await config.GetAsync(ct);
        var credits = await entitlements.UserCreditsAsync(openid, ct);
        var result = new LoginResultDto
        {
            Openid = openid,
            User = new UserDto
            {
                Nickname = user.NickName ?? "",
                Avatar = user.AvatarUrl ?? "",
                DisclaimerAckAt = user.DisclaimerAgreedAt ?? 0,
                PrivacyAckAt = user.PrivacyAgreedAt ?? 0,
                Setting = ParseSetting(user.Setting),
            },
            Families = memberRows
                .Select(m => new FamilyListItemDto
                {
                    Fid = m.FamilyId,
                    Name = families.FirstOrDefault(f => f.FamilyId == m.FamilyId)?.Name ?? "我家",
                    Role = m.Role,
                })
                .ToList(),
            Entitlement = new EntitlementDto { MemberLevel = "free", Credits = credits },
            Config = cfg,
            Token = jwt.Issue(openid),
            RefreshToken = jwt.IssueRefresh(openid),
            ServerTs = clock.NowMs,
        };
        return result;
    }

    private static UserSettingDto ParseSetting(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new UserSettingDto();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new UserSettingDto
            {
                Sound = !root.TryGetProperty("sound", out var s) || s.ValueKind != System.Text.Json.JsonValueKind.False,
                Quiet = !root.TryGetProperty("quiet", out var q) || q.ValueKind != System.Text.Json.JsonValueKind.False,
                ReduceMotion = root.TryGetProperty("reduceMotion", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return new UserSettingDto();
        }
    }
}
