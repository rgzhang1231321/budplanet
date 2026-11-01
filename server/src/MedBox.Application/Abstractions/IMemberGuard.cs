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
}
