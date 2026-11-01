using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.UseCases;

/// <summary>family.info：mode 与自绑患者在这里派生（per-family 现算，不落 user）。</summary>
public sealed class FamilyInfoHandler(IAppDbContext db, IMemberGuard guard, IClock clock)
{
    public async Task<FamilyInfoDto> HandleAsync(string fid, string openid, CancellationToken ct = default)
    {
        var m = await guard.AssertMemberAsync(fid, openid, ct);

        var family = await db.Families.FirstOrDefaultAsync(f => f.FamilyId == fid, ct)
                     ?? throw new NotMemberException();

        var members = await db.FamilyMembers.Where(x => x.FamilyId == fid).ToListAsync(ct);
        var openids = members.Select(x => x.Openid).ToList();
        var users = await db.Users.Where(u => openids.Contains(u.Openid)).ToListAsync(ct);
        var patients = await db.Patients
            .Where(p => p.FamilyId == fid && p.Status == "active")
            .ToListAsync(ct);

        return new FamilyInfoDto
        {
            Fid = family.FamilyId,
            Name = family.Name,
            Role = m.Role,
            Mode = m.Mode,
            SelfUid = m.SelfUids.Length > 0 ? m.SelfUids[0] : null,
            SelfUids = m.SelfUids,
            Members = members.Select(x => new MemberDto
            {
                Openid = x.Openid,
                Nick = users.FirstOrDefault(u => u.Openid == x.Openid)?.NickName ?? "家人",
                Role = x.Role,
            }).ToList(),
            Patients = patients.Select(p => new PatientDto
            {
                Uid = p.PatientId,
                Name = p.Name,
                Alias = p.Alias ?? p.Name,
                Avatar = p.AvatarFileId,
                SelfOpenid = p.SelfOpenid,
            }).ToList(),
            // 邀请令牌只给 admin：reset_invite 是 admin 权限，令牌下发到 member/elder
            // 等于把"拉人进家"的能力发给了没有该权限的人
            InviteToken = m.Role == "admin" ? family.InviteToken : null,
            ServerTs = clock.NowMs,
        };
    }
}

public sealed class FamilyCreateHandler(IAppDbContext db, ContentSecurityChecker security, IClock clock)
{
    public async Task<object> HandleAsync(string name, string openid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "我家";

        // 家庭名会显示给全家人，是 UGC 文本，入库前过内容安全
        await security.EnsureSafeAsync([name], openid, ct);

        var fid = "F" + Guid.NewGuid().ToString("N")[..12];
        var now = clock.NowMs;
        db.Add(new Family
        {
            FamilyId = fid,
            Name = name.Trim(),
            OwnerOpenid = openid,
            InviteToken = NewToken(),
            Tier = "free",
            MemberCount = 1,
            CreatedAt = now,
        });
        db.Add(new FamilyMember
        {
            FamilyId = fid,
            Openid = openid,
            Role = "admin",
            IsPatient = false,
            JoinedAt = now,
        });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Openid == openid, ct);
        if (user is not null)
        {
            user.FamilyIds = user.FamilyIds.Append(fid).ToArray();
            user.ActiveFamilyId = fid;
        }

        await db.SaveChangesAsync(ct);
        return new { fid, role = "admin" };
    }

    public static string NewToken() => "IVT" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
}

public sealed class FamilyJoinHandler(IAppDbContext db, IClock clock)
{
    public async Task<FamilyInfoDto> HandleAsync(string fid, string ivt, string roleIntent, string? uid, string openid, FamilyInfoHandler info, CancellationToken ct = default)
    {
        // 手动输入邀请码的人只知道码、不知道 fid（他本来也不该知道）：
        // fid 为空时按邀请码反查家庭，再把它归一化成真实 fid，后面所有写入都走同一个值。
        Family? family;
        if (string.IsNullOrEmpty(fid))
        {
            if (string.IsNullOrEmpty(ivt)) throw new ValidationException("邀请码无效");
            family = await db.Families.FirstOrDefaultAsync(f => f.InviteToken == ivt, ct)
                     ?? throw new ValidationException("邀请码无效，请让家人重发一次");
        }
        else
        {
            family = await db.Families.FirstOrDefaultAsync(f => f.FamilyId == fid, ct)
                     ?? throw new ValidationException("家庭不存在");
        }

        fid = family.FamilyId;
        if (!string.IsNullOrEmpty(family.InviteToken) && !string.Equals(family.InviteToken, ivt, StringComparison.Ordinal))
            throw new ValidationException("邀请码无效");

        var now = clock.NowMs;
        var member = await db.FamilyMembers.FirstOrDefaultAsync(m => m.FamilyId == fid && m.Openid == openid, ct);
        var noop = member is not null;

        // patient 只是入参意图，入库角色恒为 admin|member|elder
        var dbRole = roleIntent == "patient" ? "elder" : (member?.Role ?? "member");

        if (member is null)
        {
            member = new FamilyMember
            {
                FamilyId = fid,
                Openid = openid,
                Role = dbRole,
                IsPatient = roleIntent == "patient",
                JoinedAt = now,
            };
            db.Add(member);
            family.MemberCount += 1;
        }
        else if (roleIntent == "patient" && member.Role != "admin")
        {
            member.Role = "elder";
            member.IsPatient = true;
        }

        if (roleIntent == "patient")
        {
            var target = await db.Patients.FirstOrDefaultAsync(p => p.FamilyId == fid && p.PatientId == uid, ct)
                         ?? await db.Patients.Where(p => p.FamilyId == fid && p.Status == "active").OrderBy(p => p.CreatedAt).FirstOrDefaultAsync(ct);
            if (target is not null && string.IsNullOrEmpty(target.SelfOpenid))
            {
                target.SelfOpenid = openid;
                member.SelfPatientId = target.PatientId;
            }
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Openid == openid, ct);
        if (user is not null && !user.FamilyIds.Contains(fid))
            user.FamilyIds = user.FamilyIds.Append(fid).ToArray();

        await db.SaveChangesAsync(ct);

        var dto = await info.HandleAsync(fid, openid, ct);
        return dto;
    }
}

/// <summary>family.list：只回我加入的家庭（role 来自 family_members）。</summary>
public sealed class FamilyListHandler(IAppDbContext db, IClock clock)
{
    public async Task<FamilyListResult> HandleAsync(string openid, CancellationToken ct = default)
    {
        var members = await db.FamilyMembers.Where(m => m.Openid == openid).ToListAsync(ct);
        var fids = members.Select(m => m.FamilyId).ToList();
        var families = await db.Families.Where(f => fids.Contains(f.FamilyId)).ToListAsync(ct);

        return new FamilyListResult
        {
            Families = members.Select(m => new FamilyListItemDto
            {
                Fid = m.FamilyId,
                Name = families.FirstOrDefault(f => f.FamilyId == m.FamilyId)?.Name ?? "我家",
                Role = m.Role,
            }).ToList(),
            ServerTs = clock.NowMs,
        };
    }
}

/// <summary>
/// family.update_role：仅 admin。
/// 只改**权限**，不动 patients.self_openid —— 契约 4.1：mode 由"有没有自绑服药人"派生，
/// role 只决定能不能管别人。把一位自绑患者的人降成 member，他仍然看老人端主题，这是对的：
/// 他确实在吃药，把他推进 12px 字号才是 bug。
/// </summary>
public sealed class FamilyUpdateRoleHandler(IAppDbContext db, IMemberGuard guard, IClock clock)
{
    public async Task<object> HandleAsync(string fid, string openid, string targetOpenid, string role, CancellationToken ct = default)
    {
        await guard.AssertMemberAsync(fid, openid, ct, "admin");

        if (role is not ("admin" or "member" or "elder")) throw new ValidationException("角色不对");
        if (string.IsNullOrWhiteSpace(targetOpenid)) throw new ValidationException("缺少要调整的家人");

        var target = await db.FamilyMembers.FirstOrDefaultAsync(m => m.FamilyId == fid && m.Openid == targetOpenid, ct)
                     ?? throw new ValidationException("这位家人不在家庭里");

        if (target.Role == role) return new { ok = true }; // 幂等

        // 最后一个 admin 不能被降级：降完这个家庭再也没人能管（改不动药、也救不回误判的漏服）
        if (target.Role == "admin" && role != "admin")
        {
            var admins = await db.FamilyMembers.CountAsync(m => m.FamilyId == fid && m.Role == "admin", ct);
            if (admins <= 1) throw new ValidationException("至少要留一个管理员");
        }

        var before = target.Role;
        target.Role = role;

        db.Add(new AuditLog
        {
            LogId = Guid.NewGuid().ToString("N"),
            Openid = openid,
            FamilyId = fid,
            Action = "family.update_role",
            ResultCode = 0,
            Detail = $"{{\"target\":\"{targetOpenid}\",\"from\":\"{before}\",\"to\":\"{role}\"}}",
            CreatedAt = clock.NowMs,
        });

        await db.SaveChangesAsync(ct);
        return new { ok = true };
    }
}

/// <summary>
/// family.leave：退出家庭。
/// 三条必须处理的边界，少一条就会造出"没人能管"的孤儿家庭：
/// ① 只剩自己 → 拒绝（退出后这个家庭的药单永久锁死）；
/// ② 唯一 admin 退出 → 管理员移交给最早加入的其他人；
/// ③ 我自绑的服药人 → 解绑（记录留着，家里其他人还要照着这张药单照顾人）。
/// </summary>
public sealed class FamilyLeaveHandler(IAppDbContext db, IClock clock)
{
    public async Task<object> HandleAsync(string fid, string openid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fid)) throw new ValidationException("缺少 fid");

        var members = await db.FamilyMembers.Where(m => m.FamilyId == fid).ToListAsync(ct);
        var me = members.FirstOrDefault(m => m.Openid == openid);
        if (me is null) throw new NotMemberException();

        if (members.Count == 1) throw new ValidationException("家庭里只有你，退出后这张药单就没人管了");

        if (me.Role == "admin" && members.Count(m => m.Role == "admin") <= 1)
        {
            var heir = members.Where(m => m.Openid != openid).OrderBy(m => m.JoinedAt).First();
            heir.Role = "admin";
        }

        db.Remove(me);

        var bound = await db.Patients.Where(p => p.FamilyId == fid && p.SelfOpenid == openid).ToListAsync(ct);
        foreach (var p in bound) p.SelfOpenid = null;

        var family = await db.Families.FirstOrDefaultAsync(f => f.FamilyId == fid, ct);
        if (family is not null) family.MemberCount = Math.Max(0, family.MemberCount - 1);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Openid == openid, ct);
        if (user is not null)
        {
            user.FamilyIds = user.FamilyIds.Where(x => x != fid).ToArray();
            if (user.ActiveFamilyId == fid) user.ActiveFamilyId = user.FamilyIds.FirstOrDefault();
        }

        db.Add(new AuditLog
        {
            LogId = Guid.NewGuid().ToString("N"),
            Openid = openid,
            FamilyId = fid,
            Action = "family.leave",
            ResultCode = 0,
            Detail = "{}",
            CreatedAt = clock.NowMs,
        });

        await db.SaveChangesAsync(ct);
        return new { ok = true };
    }
}

/// <summary>family.reset_invite：仅 admin。换新码即让旧码立刻失效（群里转过的邀请不能再把人拉进来）。</summary>
public sealed class FamilyResetInviteHandler(IAppDbContext db, IMemberGuard guard)
{
    public async Task<object> HandleAsync(string fid, string openid, CancellationToken ct = default)
    {
        await guard.AssertMemberAsync(fid, openid, ct, "admin");

        var family = await db.Families.FirstOrDefaultAsync(f => f.FamilyId == fid, ct)
                     ?? throw new ValidationException("家庭不存在");

        family.InviteToken = FamilyCreateHandler.NewToken();
        family.InviteExpireAt = null;

        await db.SaveChangesAsync(ct);
        return new { ivt = family.InviteToken };
    }
}
