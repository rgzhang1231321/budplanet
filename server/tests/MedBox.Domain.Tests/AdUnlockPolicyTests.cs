using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

/// <summary>
/// ad.unlock 的防重放规则。参数来自 config 下发（interstitialCool / interstitialDayMax），
/// 所以这里一律按"传进来的阈值"验边界，不写死 30 秒 / 5 次 —— 写死就又把口径搬回代码里了。
/// </summary>
public class AdUnlockPolicyTests
{
    private const long Now = 1_761_955_200_000L;

    [Fact]
    public void inside_the_cool_window_is_blocked()
        => AdUnlockPolicy.InCooldown(Now, Now - 29_000, coolSeconds: 30).Should().BeTrue();

    [Fact]
    public void exactly_at_the_window_edge_is_allowed()
        // 边界开放：满 30 秒即可再领，避免"卡在 29.9 秒"这种莫名其妙的拒绝
        => AdUnlockPolicy.InCooldown(Now, Now - 30_000, coolSeconds: 30).Should().BeFalse();

    [Fact]
    public void first_ever_unlock_has_no_cooldown()
        => AdUnlockPolicy.InCooldown(Now, lastGrantAtMs: null, coolSeconds: 30).Should().BeFalse();

    [Fact]
    public void zero_or_negative_cool_means_unlimited()
    {
        AdUnlockPolicy.InCooldown(Now, Now, coolSeconds: 0).Should().BeFalse();
        AdUnlockPolicy.InCooldown(Now, Now, coolSeconds: -1).Should().BeFalse();
    }

    [Fact]
    public void day_max_is_inclusive_of_the_limit()
    {
        AdUnlockPolicy.ExceedsDayMax(todayCount: 3, dayMax: 3).Should().BeTrue();
        AdUnlockPolicy.ExceedsDayMax(todayCount: 2, dayMax: 3).Should().BeFalse();
    }

    [Fact]
    public void zero_day_max_means_unlimited()
        => AdUnlockPolicy.ExceedsDayMax(todayCount: 99, dayMax: 0).Should().BeFalse();
}
