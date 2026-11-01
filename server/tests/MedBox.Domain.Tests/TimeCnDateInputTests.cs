using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

/// <summary>
/// 日期入参的校验口径。非法值必须被拦在边界（→1005 "参数错误"），
/// 一旦漏进 TimeCn.DayStartUtcMs 的 ParseExact 就抛 FormatException，被映射成 1009"服务异常"，
/// 前端会把它当网络故障写进重试队列无限重投。
/// </summary>
public class TimeCnDateInputTests
{
    [Theory]
    [InlineData("2026-11-01")]
    [InlineData("2026-01-31")]
    [InlineData("2024-02-29")] // 闰日
    public void legal_iso_dates_are_accepted(string input)
    {
        TimeCn.TryParseDate(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(input);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026/11/01")]
    [InlineData("2026-11-1")]      // 少补零：前端应统一成 ISO，别在这里猜
    [InlineData("2026-13-01")]     // 月越界
    [InlineData("2026-02-30")]     // 日期不存在
    [InlineData("20261101")]
    [InlineData("today")]
    public void illegal_dates_are_rejected_so_the_caller_returns_1005(string? input)
    {
        TimeCn.TryParseDate(input, out var normalized).Should().BeFalse();
        normalized.Should().BeEmpty();

        // 反向确认：这些值直接进解析器会抛 FormatException（1009 的来源）
        if (!string.IsNullOrWhiteSpace(input))
            FluentActions.Invoking(() => TimeCn.DayStartUtcMs(input)).Should().Throw<FormatException>();
    }
}
