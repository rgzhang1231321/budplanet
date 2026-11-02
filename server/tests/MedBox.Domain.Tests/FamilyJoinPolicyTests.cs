using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

/// <summary>
/// family.join 的邀请码判定。规则核心只有两条：
/// ① 邀请码是"给还没进这个家的人"的凭证；② 已经是成员的人重复 join 必须放行（幂等）。
/// 之前先验码后查成员，导致前端 bindSelf 的第二次 join 带着空串被 1005 顶回 ——
/// 把一次本来就合法的调用变成"邀请码无效"。
///
/// 空令牌分支在 V011 之前是"放行"（历史家庭没码就不拦），等于凭 fid 就能加入。
/// V011 已回填存量家庭的 invite_token，本处同步改为"没码就拒"，双保险收口。
/// </summary>
public sealed class FamilyJoinPolicyTests
{
    private const string Token = "IVT1A2B3C4D";

    [Theory]
    [InlineData(Token, "")]
    [InlineData(Token, "IVTWRONG12")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void 已成员带任何邀请码都能重复加入(string? familyToken, string? ivt)
        => FamilyJoinPolicy.CanJoin(isMember: true, familyToken, ivt).Should().BeTrue();

    [Theory]
    [InlineData(Token, Token)]
    public void 新成员带对的邀请码能加入(string familyToken, string ivt)
        => FamilyJoinPolicy.CanJoin(isMember: false, familyToken, ivt).Should().BeTrue();

    [Theory]
    [InlineData(Token, "")]
    [InlineData(Token, "IVTWRONG12")]
    [InlineData(Token, "ivt1a2b3c4d")]  // 码是逐字比较，大小写不同就是不对
    [InlineData("IVTZZZZZZZZ", null)]
    [InlineData("", "")]
    [InlineData("", null)]
    [InlineData(null, "")]
    [InlineData(null, null)]
    public void 新成员邀请码不对必须拒绝(string? familyToken, string? ivt)
        => FamilyJoinPolicy.CanJoin(isMember: false, familyToken, ivt).Should().BeFalse();

    /// <summary>放宽的口子只能开给"已是成员"，不能因为 ivt 为空之类的入参放宽。</summary>
    [Fact]
    public void 跳过校验的前提只有成员身份()
    {
        FamilyJoinPolicy.CanJoin(isMember: false, Token, "").Should().BeFalse();
        FamilyJoinPolicy.CanJoin(isMember: true, Token, "").Should().BeTrue();
    }

    /// <summary>
    /// 家庭没令牌（NULL 或空串）时**不再放行**：V011 已给存量家庭回填 invite_token，
    /// 走到这里只剩"真的没有码"，而没码可验 = 任何人凭 fid 都能进，这是必须堵住的口子。
    /// 堵住之后历史数据已被脚本补齐，所以正常链路不会命中。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void 家庭没有令牌时新成员一律拒绝(string? familyToken)
        => FamilyJoinPolicy.CanJoin(isMember: false, familyToken, "IVTANYTHING").Should().BeFalse();

    /// <summary>成员短路优先于空令牌拒绝：没码的家庭里，老成员仍能幂等 join（bindSelf 靠它）。</summary>
    [Fact]
    public void 空令牌家庭的成员仍然短路放行()
    {
        FamilyJoinPolicy.CanJoin(isMember: true, "", "IVTANYTHING").Should().BeTrue();
        FamilyJoinPolicy.CanJoin(isMember: true, null, "").Should().BeTrue();
    }
}
