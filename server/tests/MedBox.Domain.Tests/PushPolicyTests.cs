using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

public class PushPolicyTests
{
    [Fact]
    public void errcode_zero_is_delivered() => PushPolicy.Decide(0).Should().Be(PushOutcome.Delivered);

    [Theory]
    [InlineData(43101)] // 用户拒收
    [InlineData(43004)] // 未订阅
    [InlineData(40003)] // openid 无效
    public void unrecoverable_errors_are_dropped(int code)
        => PushPolicy.Decide(code).Should().Be(PushOutcome.Drop);

    [Fact]
    public void api_rate_limit_retries_with_backoff()
    {
        PushPolicy.Decide(45009).Should().Be(PushOutcome.Retry);
        PushPolicy.RetryDelayMs(0).Should().Be(60_000);
        PushPolicy.RetryDelayMs(1).Should().Be(120_000);
        PushPolicy.RetryDelayMs(2).Should().Be(240_000);
        PushPolicy.RetryDelayMs(9).Should().Be(600_000); // 封顶 10 分钟
    }

    [Theory]
    [InlineData(47003)] // 模板参数不合法
    [InlineData(-2)]    // 本地：模板未配置
    public void template_errors_do_not_retry(int code)
        => PushPolicy.Decide(code).Should().Be(PushOutcome.Fail);

    [Fact]
    public void missing_access_token_retries()
        => PushPolicy.Decide(-3).Should().Be(PushOutcome.Retry);

    [Fact]
    public void task_id_is_stable_and_unique_per_minute()
    {
        var a = PushPolicy.TaskId("openid1", 100, "CHECKIN");
        var b = PushPolicy.TaskId("openid1", 100, "CHECKIN");
        var c = PushPolicy.TaskId("openid1", 101, "CHECKIN");
        var d = PushPolicy.TaskId("openid1", 100, "MISSED");

        a.Should().Be(b);
        a.Should().NotBe(c);
        a.Should().NotBe(d);
        a.Should().HaveLength(32);
    }

    [Fact]
    public void scoped_task_id_is_stable_across_minutes_but_unique_per_week()
    {
        // 周报按 week_key 幂等：Worker 重启后"分钟桶"变了也不能把上周周报再发一遍
        var a = PushPolicy.ScopedTaskId("openid1", "2026-W44", "WEEKLY");
        var b = PushPolicy.ScopedTaskId("openid1", "2026-W44", "WEEKLY");
        var c = PushPolicy.ScopedTaskId("openid1", "2026-W45", "WEEKLY");
        var d = PushPolicy.ScopedTaskId("openid2", "2026-W44", "WEEKLY");

        a.Should().Be(b);
        a.Should().NotBe(c);
        a.Should().NotBe(d);
        a.Should().HaveLength(32);
        // 与分钟桶键不得相互碰撞（域串带 # 前缀隔开）
        a.Should().NotBe(PushPolicy.TaskId("openid1", 2026, "WEEKLY"));
    }
}

public class WeeklySummaryTests
{
    [Fact]
    public void folds_rate_and_missed_into_one_line()
        => WeeklySummary.Build(11, 12, 1).Should().Be("完成率92% 漏服1次");

    [Fact]
    public void rounds_half_away_from_zero()
        => WeeklySummary.Build(1, 8, 7).Should().Be("完成率13% 漏服7次");

    [Fact]
    public void zero_total_does_not_divide_by_zero()
        => WeeklySummary.Build(0, 0, 0).Should().Be("完成率0% 漏服0次");

    [Fact]
    public void three_digit_rate_still_fits()
    {
        var s = WeeklySummary.Build(999, 999, 999);
        s.Should().Be("完成率100% 漏服999次");
        s.Length.Should().BeLessThanOrEqualTo(WeeklySummary.MaxLen);
    }
}

public class WeekRangeTests
{
    [Fact]
    public void range_text_spans_monday_to_sunday()
        => TimeCn.WeekRangeText("2026-10-26").Should().Be("10.26-11.01"); // 跨月

    [Fact]
    public void range_text_crosses_year()
        => TimeCn.WeekRangeText("2025-12-29").Should().Be("12.29-01.04");

    [Fact]
    public void week_end_is_next_monday_midnight_cn()
        => TimeCn.WeekEndUtcMs("2026-10-26").Should().Be(TimeCn.DayStartUtcMs("2026-11-02"));
}

public class QuietHoursTests
{
    [Theory]
    [InlineData(22 * 60)]      // 22:00
    [InlineData(23 * 60 + 59)]
    [InlineData(0)]
    [InlineData(6 * 60 + 59)]
    public void inside_quiet_window(int minute)
        => QuietHours.Contains("22:00", "07:00", minute).Should().BeTrue();

    [Theory]
    [InlineData(7 * 60)]
    [InlineData(8 * 60)]
    [InlineData(21 * 60 + 59)]
    public void outside_quiet_window(int minute)
        => QuietHours.Contains("22:00", "07:00", minute).Should().BeFalse();

    [Fact]
    public void same_start_end_means_disabled()
        => QuietHours.Contains("07:00", "07:00", 7 * 60).Should().BeFalse();
}

public class WeekKeyTests
{
    [Fact]
    public void iso_week_key()
    {
        TimeCn.WeekKey("2026-10-30").Should().Be("2026-W44");
        TimeCn.WeekKey("2026-10-26").Should().Be("2026-W44"); // 周一
        TimeCn.WeekKey("2026-11-01").Should().Be("2026-W44"); // 同周周日
        TimeCn.WeekKey("2026-11-02").Should().Be("2026-W45");
    }
}
