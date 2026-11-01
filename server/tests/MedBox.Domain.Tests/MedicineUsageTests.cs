using MedBox.Domain;
using Xunit;

namespace MedBox.Domain.Tests;

/// <summary>
/// 药名归一与「常用药」候选合并（P22 chips + 药名联想）。
/// 锁三件会被用户直接看见的事：同名不重复出现、自家药排在全平台榜前面、空药名不占名额。
/// </summary>
public class MedicineUsageTests
{
    private sealed record Item(string Name);

    [Theory]
    [InlineData("阿司匹林", "阿司匹林")]
    [InlineData("  阿司匹林  ", "阿司匹林")]
    [InlineData("　阿司匹林　", "阿司匹林")] // 全角空格：老人输入法切错时很常见
    [InlineData("VitD", "vitd")]
    [InlineData(null, "")]
    public void 归一键去空白且小写(string? raw, string expected)
        => Assert.Equal(expected, MedicineUsage.Normalize(raw));

    [Fact]
    public void 空关键词命中所有_非空关键词按包含匹配()
    {
        Assert.True(MedicineUsage.Matches("阿司匹林肠溶片", ""));
        Assert.True(MedicineUsage.Matches("阿司匹林肠溶片", "阿司"));
        Assert.True(MedicineUsage.Matches("阿司匹林肠溶片", "肠溶"));
        Assert.False(MedicineUsage.Matches("阿司匹林肠溶片", "二甲双胍"));
    }

    [Fact]
    public void 自家药排在前面_同名只出现一次()
    {
        var items = MedicineUsage.Merge(
        [
            new[] { new Item("布洛芬缓释胶囊") },
            new[] { new Item("苯磺酸氨氯地平片"), new Item(" 布洛芬缓释胶囊 ") },
        ], x => x.Name, MedicineUsage.CommonTop);

        Assert.Equal(["布洛芬缓释胶囊", "苯磺酸氨氯地平片"], items.Select(x => x.Name));
    }

    [Fact]
    public void 超过上限截断_空药名不占名额()
    {
        var items = MedicineUsage.Merge(
        [
            new[] { new Item(""), new Item("  ") },
            new[] { new Item("药一"), new Item("药二"), new Item("药三") },
        ], x => x.Name, 2);

        Assert.Equal(["药一", "药二"], items.Select(x => x.Name));
    }

    [Fact]
    public void 上榜门槛是三个净添加()
        => Assert.Equal(3, MedicineUsage.MinCountToRank);
}
