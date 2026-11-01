using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

/// <summary>
/// family.join 的邀请码判定。规则核心只有一条：
/// 邀请码是"给还没进这个家的人"的凭证；已经是成员的人重复 join 必须放行（幂等）。
/// 之前先验码后查成员，导致前端 bindSelf 的第二次 join 带着空串被 1005 顶回 ——
/// 把一次本来就合法的调用变成"邀请码无效"。
/// </summary>
public sealed class FamilyJoinPolicyTests
{
    private const string Token = "IVT1A2B3C4D";

    [Theory]
    [InlineData(Token, "")]
    [InlineData(Token, "IVTWRONG12")]
    [InlineData("", "")]
    public void 已成员带任何邀请码都能重复加入(string familyToken, string ivt)
        => FamilyJoinPolicy.CanJoin(isMember: true, familyToken, ivt).Should().BeTrue();

    [Theory]
    [InlineData(Token, Token)]
    [InlineData("", "")]  // 历史家庭还没生成令牌：没有码可验，不拦
    public void 新成员带对的邀请码能加入(string familyToken, string ivt)
        => FamilyJoinPolicy.CanJoin(isMember: false, familyToken, ivt).Should().BeTrue();

    [Theory]
    [InlineData(Token, "")]
    [InlineData(Token, "IVTWRONG12")]
    [InlineData(Token, "ivt1a2b3c4d")]  // 码是逐字比较，大小写不同就是不对
    [InlineData("IVTZZZZZZZZ", null)]
    public void 新成员邀请码不对必须拒绝(string familyToken, string? ivt)
        => FamilyJoinPolicy.CanJoin(isMember: false, familyToken, ivt).Should().BeFalse();

    /// <summary>放宽的口子只能开给"已是成员"，不能因为 ivt 为空之类的入参放宽。</summary>
    [Fact]
    public void 跳过校验的前提只有成员身份()
    {
        FamilyJoinPolicy.CanJoin(isMember: false, Token, "").Should().BeFalse();
        FamilyJoinPolicy.CanJoin(isMember: true, Token, "").Should().BeTrue();
    }
}
