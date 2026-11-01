using MedBox.Application.Abstractions;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.Services;

/// <summary>
/// 家庭关系闸门。任何涉及 fid 的用例第一行都要过它：
/// 非成员 → 1002 并落审计；角色不符 → 1003 并落审计。
/// </summary>
public sealed class MemberGuard(IAppDbContext db, IClock clock) : IMemberGuard
{
    public async Task<Membership> AssertMemberAsync(string familyId, string openid, CancellationToken ct = default, params string[] allowedRoles)
    {
        if (string.IsNullOrWhiteSpace(familyId)) throw new ValidationException("缺少 fid");

        var member = await db.FamilyMembers
            .FirstOrDefaultAsync(m => m.FamilyId == familyId && m.Openid == openid, ct);

        if (member is null)
        {
            await AuditAsync(openid, familyId, "assert_member", 1002, ct);
            throw new NotMemberException();
        }

        if (allowedRoles.Length > 0 && !allowedRoles.Contains(member.Role))
        {
            await AuditAsync(openid, familyId, "assert_role:" + string.Join('|', allowedRoles), 1003, ct);
            throw new ForbiddenException();
        }
        // selfUids 必须是数组：同一 openid 在一个家庭内可能绑多位 patient（本人 + 代管）
        var selfUids = await db.Patients
            .Where(p => p.FamilyId == familyId && p.SelfOpenid == openid && p.Status == "active")
            .Select(p => p.PatientId)
            .ToListAsync(ct);

        var patientIds = await db.Patients
            .Where(p => p.FamilyId == familyId && p.Status == "active")
            .Select(p => p.PatientId)
            .ToListAsync(ct);

        return new Membership(familyId, member.Role, selfUids.ToArray(), patientIds.ToArray());
    }

    public Task AuditForbiddenAsync(string familyId, string openid, string action, CancellationToken ct = default)
        => AuditAsync(openid, familyId, action, 1003, ct);

    private async Task AuditAsync(string openid, string familyId, string action, int code, CancellationToken ct)
    {
        db.Add(new AuditLog
        {
            LogId = Guid.NewGuid().ToString("N"),
            Openid = openid,
            FamilyId = familyId,
            Action = action,
            ResultCode = code,
            Detail = "{}",
            CreatedAt = clock.NowMs,
        });
        await db.SaveChangesAsync(ct);
    }
}
