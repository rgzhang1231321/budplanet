using MedBox.Application.Abstractions;
using MedBox.Domain;

namespace MedBox.Application.Services;

/// <summary>
/// UGC 文本进库前的内容安全闸门（后端方案 §7：服务端调 msgSecCheck，87014 命中即拒）。
/// 口径：只拦"确定违规"（suggest=risky），转为 1005 让用户改字；
/// 接口不可用 / 未配置一律放行（由 <see cref="IWxApiClient"/> 实现方决定并记 Warning）——
/// 微信抖动时让用户存不了药，比留一条待复核的备注危险得多。
/// </summary>
public sealed class ContentSecurityChecker(IWxApiClient wx)
{
    /// <summary>逐个校验非空文本，命中违禁即抛 <see cref="ValidationException"/>（→1005）。</summary>
    public async Task EnsureSafeAsync(IEnumerable<string?> texts, string openid, CancellationToken ct = default)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var verdict = await wx.MsgSecCheckAsync(text.Trim(), openid, ct);
            if (verdict == WxSecVerdict.Risky)
                throw new ValidationException("这段文字包含不适宜内容，请改一下再保存");
        }
    }
}
