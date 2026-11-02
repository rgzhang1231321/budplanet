namespace MedBox.Domain;

/// <summary>
/// family.join 的邀请码判定（纯函数，可单测）。
/// 邀请码唯一的作用是给"还没进这个家"的人一个凭证；对已经是成员的人再验一遍没有任何安全收益，
/// 只会把幂等调用变成 1005 —— 前端 bindSelf 会带着 fid 再 join 一次，而 invite_token 现在只下发给 admin，
/// 非 admin 手里是空串。所以：先判成员身份，是成员就跳过校验，只有新成员才验码。
///
/// 成员短路与"没码就拒"是两条独立的规则，缺一不可：
/// 前者保证重复 join 幂等（bindSelf 依赖它），后者保证令牌为空的家庭不会被任何人凭 fid 混入。
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
    /// 家庭没有令牌（NULL 或空串）时**拒绝**：没有码可验就不能放行，否则等于凭 fid 就能加入。
    /// 这条分支在 V011 之前是"不拦"，与 V011 回填脚本一起收口（双保险）：
    /// 脚本保证存量家庭都有码，这里保证"万一还是空码"也进不来。
    /// </summary>
    private static bool TokenMatches(string? familyInviteToken, string? ivt)
        => !string.IsNullOrEmpty(familyInviteToken) && string.Equals(familyInviteToken, ivt, StringComparison.Ordinal);
}
