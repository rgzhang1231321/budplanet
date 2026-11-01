using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.UseCases;

public sealed class PatientListHandler(IAppDbContext db, IClock clock)
{
    public async Task<object> HandleAsync(string fid, string openid, IMemberGuard guard, CancellationToken ct = default)
    {
        await guard.AssertMemberAsync(fid, openid, ct);
        var rows = await db.Patients.Where(p => p.FamilyId == fid && p.Status == "active").ToListAsync(ct);
        return new
        {
            patients = rows.Select(p => new PatientDto
            {
                Uid = p.PatientId,
                Name = p.Name,
                Alias = p.Alias ?? p.Name,
                Avatar = p.AvatarFileId,
                SelfOpenid = p.SelfOpenid,
            }).ToList(),
            server_ts = clock.NowMs,
        };
    }
}

/// <summary>服药人不设限（多人协同是唯一增长杠杆，对它收费等于自断传播）。称呼仍需过内容安全。</summary>
public sealed class PatientCreateHandler(IAppDbContext db, IMemberGuard guard, ContentSecurityChecker security, IClock clock)
{
    public async Task<object> HandleAsync(string fid, string openid, string name, string? alias, CancellationToken ct = default)
    {
        await guard.AssertMemberAsync(fid, openid, ct, "admin", "member");
        if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("填一下称呼");

        await security.EnsureSafeAsync([name, alias], openid, ct);

        var uid = "P" + Guid.NewGuid().ToString("N")[..10];
        db.Add(new Patient
        {
            PatientId = uid,
            FamilyId = fid,
            Name = name.Trim(),
            Alias = string.IsNullOrWhiteSpace(alias) ? name.Trim() : alias.Trim(),
            ChronicTags = [],
            Status = "active",
            CreatedAt = clock.NowMs,
        });
        await db.SaveChangesAsync(ct);
        return new { uid };
    }
}

public sealed class PatientUpdateHandler(IAppDbContext db, IMemberGuard guard, ContentSecurityChecker security)
{
    public async Task<object> HandleAsync(string fid, string openid, string uid, string name, string? alias, string? avatar, CancellationToken ct = default)
    {
        await guard.AssertMemberAsync(fid, openid, ct, "admin", "member");
        if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("填一下称呼");

        await security.EnsureSafeAsync([name, alias], openid, ct);

        var p = await db.Patients.FirstOrDefaultAsync(x => x.FamilyId == fid && x.PatientId == uid && x.Status == "active", ct)
                ?? throw new ValidationException("服药人不存在");

        p.Name = name.Trim();
        p.Alias = string.IsNullOrWhiteSpace(alias) ? p.Name : alias.Trim();
        // avatar 传 null 表示"这次不改"，传空串表示"清掉头像"
        if (avatar is not null) p.AvatarFileId = string.IsNullOrWhiteSpace(avatar) ? null : avatar;

        await db.SaveChangesAsync(ct);
        return new { uid = p.PatientId };
    }
}

/// <summary>
/// patient.archive：软删。
/// 连带做两件事，否则会留下"点不动的药"：① 把他名下计划置 finished；② 删掉未到点的 pending 行。
/// 已过点与已打卡的行**不删** —— 周报、历史、给医生看的报告都还要能回溯。
/// </summary>
public sealed class PatientArchiveHandler(IAppDbContext db, IMemberGuard guard, IClock clock)
{
    public async Task<object> HandleAsync(string fid, string openid, string uid, CancellationToken ct = default)
    {
        await guard.AssertMemberAsync(fid, openid, ct, "admin", "member");

        var p = await db.Patients.FirstOrDefaultAsync(x => x.FamilyId == fid && x.PatientId == uid, ct)
                ?? throw new ValidationException("服药人不存在");
        if (p.Status == "archived") return new { ok = true }; // 幂等

        p.Status = "archived";

        var plans = await db.Plans.Where(x => x.FamilyId == fid && x.PatientId == uid && x.Status == "active").ToListAsync(ct);
        foreach (var pl in plans) pl.Status = "finished";

        var now = clock.NowMs;
        var stale = await db.DoseLogs
            .Where(d => d.FamilyId == fid && d.PatientId == uid && d.Status == DoseStatusWire.Pending && d.DueTs > now)
            .ToListAsync(ct);
        foreach (var row in stale) db.Remove(row);

        await db.SaveChangesAsync(ct);
        return new { ok = true };
    }
}
