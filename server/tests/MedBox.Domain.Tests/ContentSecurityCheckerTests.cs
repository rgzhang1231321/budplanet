using FluentAssertions;
using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Domain;
using NSubstitute;

namespace MedBox.Domain.Tests;

/// <summary>
/// 内容安全闸门。测的是"什么该拦"：只有 definitely-risky 转 1005，
/// 空白文本不打微信接口；放行路径不许顺手抛错（微信抖动时药还得能存）。
/// </summary>
public class ContentSecurityCheckerTests
{
    private const string Openid = "openid_1";

    [Fact]
    public async Task risky_text_is_rejected_as_1005()
    {
        var wx = Wx(WxSecVerdict.Risky);
        var checker = new ContentSecurityChecker(wx);

        var act = () => checker.EnsureSafeAsync(["违禁文本"], Openid);

        (await act.Should().ThrowAsync<ValidationException>())
            .Which.Code.Should().Be(1005);
    }

    [Fact]
    public async Task safe_text_passes_through()
    {
        var wx = Wx(WxSecVerdict.Pass);
        var checker = new ContentSecurityChecker(wx);

        await checker.EnsureSafeAsync(["苯磺酸氨氯地平片", "早饭后吃"], Openid);
    }

    [Fact]
    public async Task blank_text_never_calls_wechat()
    {
        var wx = Wx(WxSecVerdict.Pass);
        var checker = new ContentSecurityChecker(wx);

        await checker.EnsureSafeAsync([null, "", "   "], Openid);

        await wx.DidNotReceive().MsgSecCheckAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task every_non_empty_field_is_checked()
    {
        var wx = Wx(WxSecVerdict.Pass);
        var checker = new ContentSecurityChecker(wx);

        await checker.EnsureSafeAsync(["药名", null, "备注"], Openid);

        await wx.Received(2).MsgSecCheckAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static IWxApiClient Wx(WxSecVerdict verdict)
    {
        var wx = Substitute.For<IWxApiClient>();
        wx.MsgSecCheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(verdict));
        return wx;
    }
}
