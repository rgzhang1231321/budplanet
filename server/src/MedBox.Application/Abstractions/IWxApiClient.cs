namespace MedBox.Application.Abstractions;

/// <summary>微信接口边界。测试里必须用替身，绝不打真实接口。</summary>
public interface IWxApiClient
{
    /// <summary>code2session：换 openid（前端永不传 openid，身份只从 code 换）。</summary>
    Task<WxSession> Code2SessionAsync(string code, CancellationToken ct = default);

    /// <summary>订阅消息下发。返回值区分 errcode，供额度池判决。</summary>
    Task<WxSendResult> SendSubscribeAsync(string openid, string tmplId, string page, IReadOnlyDictionary<string, string> data, CancellationToken ct = default);
}

public sealed record WxSession(string Openid, string? Unionid, string? SessionKey);

public sealed record WxSendResult(bool Ok, int ErrCode, string ErrMsg);
