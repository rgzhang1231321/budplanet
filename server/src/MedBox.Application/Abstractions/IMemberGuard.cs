namespace MedBox.Application.Abstractions;

/// <summary>member 关系（一次查询拿到 mode 与 selfUids 所需的全部信息）。</summary>
public sealed record Membership(
    string FamilyId,
    string Role,
    string[] SelfUids,
    string[] PatientIds)
{
    public bool IsElder => SelfUids.Length > 0;

    /// <summary>mode 只决定渲染；role 只决定权限，二者解耦。</summary>
    public string Mode => IsElder ? "elder" : "guardian";
}

public interface IMemberGuard
{
    /// <summary>任何涉及 fid 的用例第一行调用它。非成员抛 1002，权限不足抛 1003 并落审计。</summary>
    Task<Membership> AssertMemberAsync(string familyId, string openid, CancellationToken ct = default, params string[] allowedRoles);

    /// <summary>
    /// 用例自己判出的越权（1003）补落审计 —— 例如 elder 只能记自己的药、只能延后自己的药。
    /// 只在 AssertMember 已经放行之后调用；AssertMember 内部判的 1002/1003 已经写过，别重复写。
    /// </summary>
    Task AuditForbiddenAsync(string familyId, string openid, string action, CancellationToken ct = default);
}
