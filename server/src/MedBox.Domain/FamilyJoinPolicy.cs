namespace MedBox.Domain;

/// <summary>
/// family.join 的邀请码判定（纯函数，可单测）。
/// 邀请码唯一的作用是给"还没进这个家"的人一个凭证；对已经是成员的人再验一遍没有任何安全收益，
/// 只会把幂等调用变成 1005 —— 前端 bindSelf 会带着 fid 再 join 一次，而 invite_token 现在只下发给 admin，
/// 非 admin 手里是空串。所以：先判成员身份，是成员就跳过校验，只有新成员才验码。
/// </summary>
public static class FamilyJoinPolicy
{
    /// <summary>
    /// 能不能进这个家。<paramref name="isMember"/> 必须来自
    /// 「family_id = fid 且 openid = 当前登录人」的查询结果，不能只按 fid 判。
    /// </summary>
    public static bool CanJoin(bool isMember, string? familyInviteToken, string? ivt)
        => isMember || TokenMatches(familyInviteToken, ivt);

    /// <summary>
    /// 家庭还没生成令牌时不拦（没有码可验）；有令牌就逐字相等。
    /// ⚠️ 这不是纯防御分支：`families.invite_token` 可空，令牌为 NULL 的家庭等于**凭 fid 就能加入**。
    /// 与本次改动前逐字等价、非本次引入，但已登记为批 4 待决（三选一：没码就拒 / info 时补生成 /
    /// 明确接受并写进契约）。别把它当"历史数据兜底"顺手扩到别处。
    /// </summary>
    private static bool TokenMatches(string? familyInviteToken, string? ivt)
        => string.IsNullOrEmpty(familyInviteToken) || string.Equals(familyInviteToken, ivt, StringComparison.Ordinal);
}
