using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
                DisplayMode = p.DisplayMode,
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

public sealed class FamilyJoinHandler(IAppDbContext db, IClock clock, ILogger<FamilyJoinHandler> logger)
{
    /// <summary>
    /// family.join —— 只负责"进这个家"，**不改已有成员的角色**。
    /// 历史上这里顺带把非 admin 成员降级成 elder（<c>roleIntent=='patient'</c> 时），
    /// 而前端 bindSelf 正好传 'patient'，于是"加入家庭"把一位 member 静默降成了 elder，
    /// 丢掉 member 权限。加入家庭与改角色是两个语义，改角色只能走 family.update_role。
    /// 绑定 self（patients.self_openid）仍由本用例完成 —— 那才是 bindSelf 真正需要的东西。
    /// </summary>
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

        // 先判成员身份再验码：「已经是成员」的人重复 join 是幂等调用（前端 bindSelf 就是这么来的），
        // 邀请码对成员没有安全收益，只会把一次成功的加入用 1005 顶回。
        // 查询必须同时限定 family_id 与当前 openid —— 只按 fid 查等于让人凭 fid 混进别人家。
        var now = clock.NowMs;
        var member = await db.FamilyMembers.FirstOrDefaultAsync(m => m.FamilyId == fid && m.Openid == openid, ct);
        if (!FamilyJoinPolicy.CanJoin(member is not null, family.InviteToken, ivt))
            throw new ValidationException("邀请码无效");

        // 角色只在"新成员"这一刻定：受邀进来的老人第一次加入就该是 elder，这不是降级。
        // 老成员走到这里角色一律不动 —— 要改角色请走 family.update_role。
        var dbRole = roleIntent == "patient" ? "elder" : "member";

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

            // ★ 新成员这一次就必须把 self 绑上（2026-11-02 事故的老人端同形缺口）：
            // 只写 family_members、不写 patients.self_openid 的话，这位老人这一趟就是
            // "role=elder + selfUids 空"，服务端按 role 过滤时手上没有任何 pid 可用 ——
            // 结果是全家人的药名都发到他手机里（他再一打卡还会写进别人那条）。
            // 绑定落在同一次 SaveChanges（本方法末尾 166 行）里，与成员行一起提交：
            // BindSelfAsync 只做 db.Add / 改字段，不自己提交。
            if (roleIntent == "patient") await BindSelfAsync(fid, uid, openid, member, ct);
        }
        else if (roleIntent == "patient")
        {
            // 只做"绑定 self"，不改角色。见本类 summary。绑定规则见 BindSelfAsync。
            await BindSelfAsync(fid, uid, openid, member, ct);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Openid == openid, ct);
        if (user is not null && !user.FamilyIds.Contains(fid))
            user.FamilyIds = user.FamilyIds.Append(fid).ToArray();

        await db.SaveChangesAsync(ct);

        var dto = await info.HandleAsync(fid, openid, ct);
        return dto;
    }

    /// <summary>
    /// 「我本人也吃药」的自绑。**只做两件事**：找"已经绑给我的那条"（幂等复用）→ 没有就新建「我」再绑。
    ///
    /// ⚠️ 本方法**刻意不看 uid**（参数保留：`family.join` 的 uid 是契约冻结入参，不删不改名）。
    /// 为什么连"uid 有值就绑这一位"都不做：
    ///   前端要审核 1–7 天，前后端不可能同一秒上线。窗口期内
    ///   · 旧前端 + 新后端：旧前端仍在传 `uid = patients[0].uid`（=爸爸），若这里接受 uid，
    ///     爸爸此时 self_openid 恰好是空的，"没被别人绑"这道校验形同虚设 → 又绑回爸爸；
    ///   · 新前端 + 旧后端：旧后端的 `??` 兜底会抓最早创建的那位 → 同样绑回爸爸。
    ///   两条路都会让客户今天这个事故原样复发。所以这条分支**根本不看 uid**：
    ///   不管新前端传 undefined 还是旧前端传爸爸的 pid，结果都一样 —— 绑到自己的档案上，碰不到别人。
    ///
    /// 调用点有两个，都是 `roleIntent='patient'`：① `member is null`（受邀老人第一次加入，
    /// 2026-11-02 补上——之前这里不绑，他 selfUids 为空就等于不过滤，服务端把全家药都下发）；
    /// ② 已是成员（「我自己吃药」入口 / 前端 bindSelf 的第二次幂等调用）。两处都只认这一份规则。
    ///
    /// 历史事故回顾：老实现 uid 为空时用 `?? OrderBy(CreatedAt).FirstOrDefault()` 兜底，
    /// 等于把"家里的爸爸"当成"我"。**任何形式的"挑一位来绑"都不许再写回来。**
    /// </summary>
    private async Task BindSelfAsync(string fid, string? uid, string openid, FamilyMember member, CancellationToken ct)
    {
        var target = await db.Patients.FirstOrDefaultAsync(
            p => p.FamilyId == fid && p.SelfOpenid == openid && p.Status == "active", ct);

        string outcome;
        if (target is null)
        {
            target = CreateSelfPatient(fid);
            outcome = "created_new";
        }
        else
        {
            outcome = "bound_existing";
        }

        target.SelfOpenid = openid;
        member.SelfPatientId = target.PatientId;

        // uid 只用于观测：发版窗口期能直接从日志看出"还有旧客户端在传 uid"。
        // 只记"有没有传"，不记值 —— 旧客户端传的是别人的 pid，记进日志等于把别人档案写进日志。
        logger.LogInformation(
            "family.join 自绑 outcome={Outcome} uid_ignored={UidProvided} openid={Openid} fid={Fid} patient={Pid}",
            outcome, !string.IsNullOrWhiteSpace(uid), MaskOpenid(openid), fid, target.PatientId);
    }

    /// <summary>
    /// 给"我自己吃药"的人建一条属于本人的服药人档案。
    /// 名字固定「我」而不是微信昵称：① 昵称是 UGC，写库前要走内容安全（本路径没有那道闸）；
    /// ② 这条档案的语义就是"我自己"，用户在服药人列表里看到「我」最不容易再认错人。
    /// PatientId 格式与 PatientCreateHandler 保持一致（P + 10 位 guid）。
    /// </summary>
    private Patient CreateSelfPatient(string fid)
    {
        var patient = new Patient
        {
            PatientId = "P" + Guid.NewGuid().ToString("N")[..10],
            FamilyId = fid,
            Name = "我",
            Alias = "我",
            ChronicTags = [],
            Status = "active",
            DisplayMode = "normal",
            CreatedAt = clock.NowMs,
        };
        db.Add(patient);
        return patient;
    }

    /// <summary>日志里不落完整 openid。</summary>
    private static string MaskOpenid(string openid)
        => string.IsNullOrEmpty(openid) ? "***" : openid.Length <= 6 ? openid + "***" : openid[..6] + "***";
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
