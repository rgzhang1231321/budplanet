namespace MedBox.Application.Abstractions;

/// <summary>微信接口边界。测试里必须用替身，绝不打真实接口。</summary>
public interface IWxApiClient
{
    /// <summary>code2session：换 openid（前端永不传 openid，身份只从 code 换）。</summary>
    Task<WxSession> Code2SessionAsync(string code, CancellationToken ct = default);

    /// <summary>订阅消息下发。返回值区分 errcode，供额度池判决。</summary>
    Task<WxSendResult> SendSubscribeAsync(string openid, string tmplId, string page, IReadOnlyDictionary<string, string> data, CancellationToken ct = default);

    /// <summary>
    /// 内容安全校验（UGC 文本入库前过一遍）。
    /// 实现方负责"开关关闭 / 拿不到 token / 接口报错"三种情况的策略：一律返回 <see cref="WxSecVerdict.Pass"/> 并记 Warning。
    /// 调用方只需把 <see cref="WxSecVerdict.Risky"/> 当拒绝，别自己判 errcode。
    /// </summary>
    Task<WxSecVerdict> MsgSecCheckAsync(string content, string openid, CancellationToken ct = default);
}

public sealed record WxSession(string Openid, string? Unionid, string? SessionKey);

public sealed record WxSendResult(bool Ok, int ErrCode, string ErrMsg);

/// <summary>内容安全结论。只有 Risky 拦人：微信的 review（待人工复核）不应挡用户存药。</summary>
public enum WxSecVerdict
{
    Pass = 0,
    Risky = 1,
}
