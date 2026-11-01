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

    /// <summary>家庭还没生成令牌的历史数据不拦（没有码可验）；有令牌就逐字相等。</summary>
    private static bool TokenMatches(string? familyInviteToken, string? ivt)
        => string.IsNullOrEmpty(familyInviteToken) || string.Equals(familyInviteToken, ivt, StringComparison.Ordinal);
}
