using MedBox.Domain;
using Xunit;

namespace MedBox.Domain.Tests;

/// <summary>
/// 周期归一。这里的每个用例都对应过一次真实的静默失效：
/// 前端 chip 传 cycle_30、后端只认 days_n，「30 天」被存成长期用药。
/// </summary>
public class PlanCycleTests
{
    [Fact]
    public void 前端写法的_cycle_30_归一成_days_n_30天()
        => Assert.Equal(("days_n", (int?)30), PlanCycle.Parse("cycle_30", null));

    [Fact]
    public void 后端的_days_n_配合显式天数原样保留()
        => Assert.Equal(("days_n", (int?)7), PlanCycle.Parse("days_n", 7));

    [Fact]
    public void days_n_没给天数时兜底_30()
        => Assert.Equal(("days_n", (int?)30), PlanCycle.Parse("days_n", null));

    [Fact]
    public void 负数和零天数不当作周期()
    {
        Assert.Equal(("days_n", (int?)30), PlanCycle.Parse("days_n", 0));
        Assert.Equal(("long_term", null), PlanCycle.Parse("cycle_0", null));
        Assert.Equal(("long_term", null), PlanCycle.Parse("cycle_-3", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("long_term")]
    [InlineData("cycle_")]          // 只有前缀没有数字
    [InlineData("cycle_abc")]
    [InlineData("30")]              // 光给数字不算
    public void 认不出的取值一律按长期_宁可多提醒也不要误停药(string? wire)
        => Assert.Equal(("long_term", (int?)null), PlanCycle.Parse(wire, null));

    [Fact]
    public void 任意天数都能正确回到前端写法()
    {
        Assert.Equal("cycle_30", PlanCycle.ToWire("days_n", 30));
        Assert.Equal("cycle_7", PlanCycle.ToWire("days_n", 7));
        Assert.Equal("cycle_30", PlanCycle.ToWire("days_n", null)); // 兜底
        Assert.Equal("long_term", PlanCycle.ToWire("long_term", null));
        Assert.Equal("long_term", PlanCycle.ToWire(null, null));
    }

    [Fact]
    public void 入参与出参能来回转换_不丢天数()
    {
        var (type, days) = PlanCycle.Parse("cycle_14", null);
        Assert.Equal("cycle_14", PlanCycle.ToWire(type, days));
    }

    [Fact]
    public void 结束日含最后一天()
        => Assert.Equal("2026-11-28", PlanCycle.EndDate("2026-10-30", 30));

    [Fact]
    public void 结束日跨年正确()
        => Assert.Equal("2027-01-02", PlanCycle.EndDate("2026-12-20", 14));

    [Fact]
    public void 一天疗程的结束日就是开始日()
        => Assert.Equal("2026-10-30", PlanCycle.EndDate("2026-10-30", 1));

    [Fact]
    public void 长期用药没有结束日()
        => Assert.Null(PlanCycle.EndDate("2026-10-30", null));
}
