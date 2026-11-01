using System.Text.Encodings.Web;
using System.Text.Json;
using MedBox.Application.Abstractions;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.UseCases;

/// <summary>user.export 的出参（fileUrl 由 Controller 拼绝对地址，Handler 只管相对路径与过期时间）。</summary>
public sealed record ExportTicket(string Path, long ExpireAt);

/// <summary>
/// user.export：合规必须有（PRD 8.4）。
/// 只签一个无状态 token，**不在导出时就把文件写盘** —— 一是没必要（内容随时可重算），
/// 二是避免"导出目录越堆越多还得写清理任务"，而无状态 token 24h 后自己就失效了。
/// </summary>
public sealed class ExportHandler(IAppDbContext db, IExportSigner signer, IClock clock)
{
    public const long TtlMs = 24 * 3600 * 1000L;

    public async Task<ExportTicket> HandleAsync(string openid, CancellationToken ct = default)
    {
        var exists = await db.Users.AnyAsync(u => u.Openid == openid, ct);
        if (!exists) throw new ValidationException("用户不存在");

        var expireAt = clock.NowMs + TtlMs;
        var token = signer.Sign(openid, expireAt);
        return new ExportTicket("/api/user/export/download?token=" + token, expireAt);
    }

    /// <summary>
    /// 导出**本人**数据（不是"我所在家庭的全部数据"）。
    /// 边界很清楚：我是服药人 → 我的用药记录全给；我只是子女 → 我给父母打的卡不导出药名，
    /// 只给一个"我操作过 N 次"的计数。别人的病不是我的数据。
    /// </summary>
    public async Task<string> BuildJsonAsync(string openid, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Openid == openid, ct)
                   ?? throw new ValidationException("用户不存在");

        var members = await db.FamilyMembers.Where(m => m.Openid == openid).ToListAsync(ct);
        var fids = members.Select(m => m.FamilyId).ToList();
        var families = await db.Families.Where(f => fids.Contains(f.FamilyId)).ToListAsync(ct);

        var mine = await db.Patients.Where(p => p.SelfOpenid == openid).ToListAsync(ct);
        var mineIds = mine.Select(p => p.PatientId).ToList();
        var doses = mineIds.Count == 0
            ? []
            : await db.DoseLogs
                .Where(d => mineIds.Contains(d.PatientId))
                .OrderBy(d => d.DueDate).ThenBy(d => d.Slot)
                .ToListAsync(ct);

        // 我代别人操作的次数：只给计数，不带药名 —— 那不是我的健康数据
        var operatedByMe = await db.DoseLogs.CountAsync(d => d.TakenBy == openid, ct);

        var subs = await db.Subscriptions.Where(s => s.Openid == openid).ToListAsync(ct);
        var ents = await db.Entitlements.Where(e => e.Openid == openid).ToListAsync(ct);

        var payload = new Dictionary<string, object?>
        {
            ["exported_at"] = clock.NowMs,
            ["notice"] = "本文件含你的个人健康数据，请妥善保管；下载链接 24 小时后失效。",
            ["account"] = new Dictionary<string, object?>
            {
                ["openid"] = user.Openid,
                ["nick_name"] = user.NickName,
                ["created_at"] = user.CreatedAt,
                ["last_login_at"] = user.LastLoginAt,
                ["privacy_agreed_at"] = user.PrivacyAgreedAt,
                ["disclaimer_agreed_at"] = user.DisclaimerAgreedAt,
            },
            ["families"] = members.Select(m => new Dictionary<string, object?>
            {
                ["fid"] = m.FamilyId,
                ["name"] = families.FirstOrDefault(f => f.FamilyId == m.FamilyId)?.Name,
                ["role"] = m.Role,
                ["joined_at"] = m.JoinedAt,
            }).ToList(),
            ["patients"] = mine.Select(p => new Dictionary<string, object?>
            {
                ["uid"] = p.PatientId,
                ["family_id"] = p.FamilyId,
                ["name"] = p.Name,
                ["alias"] = p.Alias,
                ["status"] = p.Status,
                ["created_at"] = p.CreatedAt,
            }).ToList(),
            ["dose_records"] = doses.Select(d => new Dictionary<string, object?>
            {
                ["dose_id"] = d.DoseId,
                ["date"] = d.DueDate,
                ["due_time"] = TimeCn.HhMm(d.Slot),
                ["medicine_name"] = d.MedNameSnapshot,
                ["dose_text"] = d.DoseText,
                ["status"] = d.Status,
                ["taken_at"] = d.TakenAt,
                ["is_late"] = d.IsLate,
                ["is_corrected"] = d.IsCorrected,
            }).ToList(),
            ["subscriptions"] = subs.Select(s => new Dictionary<string, object?>
            {
                ["tmpl_key"] = s.TmplKey,
                ["quota"] = s.Quota,
                ["granted_total"] = s.GrantedTotal,
                ["main_switch"] = s.MainSwitch,
            }).ToList(),
            ["entitlements"] = ents.Select(e => new Dictionary<string, object?>
            {
                ["product_id"] = e.ProductId,
                ["credits"] = e.Credits,
                ["source"] = e.Source,
                ["expire_at"] = e.ExpireAt,
                ["created_at"] = e.CreatedAt,
            }).ToList(),
            ["operation_summary"] = new Dictionary<string, object?>
            {
                ["checkins_i_performed"] = operatedByMe,
                ["dose_records_returned"] = doses.Count,
            },
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            // 不转义中文：导出件是给人看的（也可能直接转给医生），\uXXXX 一堆没法读
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}

/// <summary>
/// user.erase：注销。软删而非物理删 —— 审计链与外键要能回溯，且这是"我不用了"而不是"抹掉历史"。
/// 三件必须做的事：
/// ① 个人身份信息清空（nick/avatar/unionid/设置/权益/同意时间）；
/// ② 退出所有家庭（成员行删除、家庭计数回退）；
/// ③ 我自绑的服药人解绑，但**记录留着** —— 家里其他人还要照着这张药单照顾人。
/// </summary>
public sealed class EraseHandler(IAppDbContext db, IClock clock)
{
    public async Task<object> HandleAsync(bool confirm, string openid, CancellationToken ct = default)
    {
        if (!confirm) throw new ValidationException("注销需要二次确认（confirm:true）");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Openid == openid, ct)
                   ?? throw new ValidationException("用户不存在");

        var now = clock.NowMs;

        var members = await db.FamilyMembers.Where(m => m.Openid == openid).ToListAsync(ct);
        var fids = members.Select(m => m.FamilyId).ToList();
        var families = await db.Families.Where(f => fids.Contains(f.FamilyId)).ToListAsync(ct);

        foreach (var m in members) db.Remove(m);
        foreach (var f in families)
            f.MemberCount = Math.Max(0, f.MemberCount - members.Count(m => m.FamilyId == f.FamilyId));

        // 管理员空缺的家庭要有人接手，否则这个家庭彻底管不了
        foreach (var fid in fids)
        {
            var rest = await db.FamilyMembers
                .Where(m => m.FamilyId == fid && m.Openid != openid)
                .OrderBy(m => m.JoinedAt)
                .ToListAsync(ct);
            if (rest.Count > 0 && rest.All(m => m.Role != "admin"))
                rest[0].Role = "admin";
            else if (rest.Count == 0)
            {
                // 家里再没有别人：这个家成了没有成员的孤儿家庭，药品/记录还在库里，
                // 但没有任何账号能再进来。必须把邀请令牌作废 —— 否则一个无人管理的
                // 家庭仍占着唯一邀请码，且原链接还能把陌生人引到一个谁也进不去的家。
                var orphan = families.FirstOrDefault(x => x.FamilyId == fid);
                if (orphan is not null)
                {
                    orphan.InviteToken = null;
                    orphan.InviteExpireAt = null;
                    orphan.MemberCount = 0;
                }
            }
        }

        var bound = await db.Patients.Where(p => p.SelfOpenid == openid).ToListAsync(ct);
        foreach (var p in bound) p.SelfOpenid = null;

        user.NickName = null;
        user.AvatarUrl = null;
        user.Unionid = null;
        user.FamilyIds = [];
        user.ActiveFamilyId = null;
        user.Setting = "{}";
        user.Entitlement = "{}";
        user.PrivacyAgreedAt = null;
        user.DisclaimerAgreedAt = null;
        user.Unreachable = true; // 注销的 openid 不再推任何订阅消息

        var subs = await db.Subscriptions.Where(s => s.Openid == openid).ToListAsync(ct);
        foreach (var s in subs)
        {
            s.Quota = 0;
            s.MainSwitch = false;
        }

        db.Add(new AuditLog
        {
            LogId = Guid.NewGuid().ToString("N"),
            Openid = openid,
            FamilyId = null,
            Action = "user.erase",
            ResultCode = 0,
            Detail = "{\"soft\":true}",
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);
        return new { ok = true, erasedAt = now };
    }
}
