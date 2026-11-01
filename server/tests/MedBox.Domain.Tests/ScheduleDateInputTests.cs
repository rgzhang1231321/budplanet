using FluentAssertions;
using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Application.UseCases;
using MedBox.Domain;
using NSubstitute;

namespace MedBox.Domain.Tests;

/// <summary>
/// date / weekStart 的入参校验落在用例边界。非法值必须抛 <see cref="ValidationException"/>（→1005 参数错误），
/// 而不是让 FormatException 冒到中间件变成 1009 —— 1009 会被前端当成服务异常写进重试队列无限重投。
/// </summary>
public class ScheduleDateInputTests
{
    private const string Fid = "f1";
    private const string Openid = "openid_1";
    private const string Today = "2026-11-01";

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("2026/11/01")]
    [InlineData("20261101")]
    [InlineData("今天")]
    public async Task today_rejects_a_malformed_date_as_1005(string badDate)
    {
        var act = () => NewTodayHandler().HandleAsync(Fid, badDate, Openid);

        var ex = await act.Should().ThrowAsync<MedBoxException>();
        ex.Which.Code.Should().Be(1005);
        ex.Which.Should().BeOfType<ValidationException>(); // 不能是 1009 走的其它异常
    }

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("2026-1-5")]
    [InlineData("not-a-date")]
    public async Task week_rejects_a_malformed_week_start_as_1005(string badStart)
    {
        var act = () => NewWeekHandler().HandleAsync(Fid, badStart, Openid);

        var ex = await act.Should().ThrowAsync<MedBoxException>();
        ex.Which.Code.Should().Be(1005);
    }

    private static TodayHandler NewTodayHandler()
    {
        var db = Substitute.For<IAppDbContext>();
        var clock = Substitute.For<IClock>();
        clock.TodayCn.Returns(Today);
        var materializer = new DoseMaterializer(db, clock);
        return new TodayHandler(db, MemberGuardStub(), materializer, clock);
    }

    private static WeekHandler NewWeekHandler()
    {
        var db = Substitute.For<IAppDbContext>();
        var clock = Substitute.For<IClock>();
        clock.TodayCn.Returns(Today);
        var materializer = new DoseMaterializer(db, clock);
        return new WeekHandler(db, MemberGuardStub(), materializer, clock);
    }

    private static IMemberGuard MemberGuardStub()
    {
        var guard = Substitute.For<IMemberGuard>();
        guard.AssertMemberAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string[]>())
            .Returns(Task.FromResult(new Membership(Fid, "admin", [], ["p1"])));
        return guard;
    }
}
