using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MedBox.Application.Abstractions;
using MedBox.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MedBox.Infrastructure.Wx;

public sealed class WxOptions
{
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string ApiBase { get; set; } = "https://api.weixin.qq.com";
    /// <summary>本地开发：未配 secret 时用固定 openid，避免每次 wx.login 的 code 变一次就多一个用户。</summary>
    public string DevOpenid { get; set; } = "dev_local_openid";
}

/// <summary>
/// 微信接口客户端：code2session / access_token 缓存 / 订阅消息下发。
/// 未配 AppSecret → 本地开发模式：登录用固定 openid，推送明确返回"拿不到 token"，绝不假装发成功。
/// </summary>
public sealed class WxApiClient(HttpClient http, IOptions<WxOptions> options, ILogger<WxApiClient> logger) : IWxApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WxOptions _opt = options.Value;

    private string? _token;
    private long _tokenExpiresAtMs;

    public async Task<WxSession> Code2SessionAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.AppSecret))
        {
            logger.LogWarning("WX__SECRET 未配置，code2session 走本地开发模式（openid={Openid}）", _opt.DevOpenid);
            return new WxSession(_opt.DevOpenid, null, null);
        }

        var url = $"{_opt.ApiBase}/sns/jscode2session?appid={_opt.AppId}&secret={_opt.AppSecret}&js_code={Uri.EscapeDataString(code)}&grant_type=authorization_code";
        var resp = await http.GetFromJsonAsync<Code2SessionResp>(url, Json, ct);
        if (resp is null || string.IsNullOrEmpty(resp.Openid))
            throw new ValidationException("微信登录失败：" + (resp?.ErrMsg ?? "no openid"));

        return new WxSession(resp.Openid, resp.Unionid, resp.SessionKey);
    }

    /// <summary>access_token 有效期 7200s，提前 5 分钟过期；进程内缓存，单实例够用（水平扩展时改 Redis）。</summary>
    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.AppSecret)) return null;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!string.IsNullOrEmpty(_token) && now < _tokenExpiresAtMs) return _token;

        var url = $"{_opt.ApiBase}/cgi-bin/token?grant_type=client_credential&appid={_opt.AppId}&secret={_opt.AppSecret}";
        var resp = await http.GetFromJsonAsync<TokenResp>(url, Json, ct);
        if (resp is null || string.IsNullOrEmpty(resp.AccessToken))
        {
            logger.LogError("获取 access_token 失败：{ErrCode} {ErrMsg}", resp?.ErrCode, resp?.ErrMsg);
            return null;
        }

        _token = resp.AccessToken;
        _tokenExpiresAtMs = now + Math.Max(60, resp.ExpiresIn - 300) * 1000L;
        return _token;
    }

    public async Task<WxSendResult> SendSubscribeAsync(string openid, string tmplId, string page, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tmplId))
            return new WxSendResult(false, PushPolicy.ErrTemplateMissing, "模板 ID 未配置");

        var token = await GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
            return new WxSendResult(false, PushPolicy.ErrNoAccessToken, "拿不到 access_token（WX__SECRET 未配置）");

        var body = new
        {
            touser = openid,
            template_id = tmplId,
            page,
            data = data.ToDictionary(kv => kv.Key, kv => new { value = kv.Value }),
            miniprogram_state = "formal",
            lang = "zh_CN",
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.ApiBase}/cgi-bin/message/subscribe/send?access_token={token}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };

        try
        {
            using var resp = await http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<SendResp>(text, Json);
            if (result is null) return new WxSendResult(false, -1, "空响应");
            return new WxSendResult(result.ErrCode == 0, result.ErrCode, result.ErrMsg ?? "");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "订阅消息发送异常 openid={Openid} tmpl={Tmpl}", openid, tmplId);
            return new WxSendResult(false, -1, "网络异常");
        }
    }

    private sealed class Code2SessionResp
    {
        [JsonPropertyName("openid")] public string? Openid { get; set; }
        [JsonPropertyName("unionid")] public string? Unionid { get; set; }
        [JsonPropertyName("session_key")] public string? SessionKey { get; set; }
        [JsonPropertyName("errcode")] public int ErrCode { get; set; }
        [JsonPropertyName("errmsg")] public string? ErrMsg { get; set; }
    }

    private sealed class TokenResp
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("errcode")] public int ErrCode { get; set; }
        [JsonPropertyName("errmsg")] public string? ErrMsg { get; set; }
    }

    private sealed class SendResp
    {
        [JsonPropertyName("errcode")] public int ErrCode { get; set; }
        [JsonPropertyName("errmsg")] public string? ErrMsg { get; set; }
    }
}
