using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MedBox.Application.Abstractions;
using MedBox.Domain;
using Microsoft.Extensions.Hosting;
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
/// 微信接口客户端：code2session / 订阅消息下发（access_token 走 <see cref="WxTokenCache"/> 单例缓存）。
/// 未配 AppSecret 只允许在 Development 降级用固定 openid；非开发环境一律拒绝登录 ——
/// 生产漏配 secret 时若继续降级，所有用户会塌成同一个 openid，能看到别人家的老人和吃药记录。
/// </summary>
public sealed class WxApiClient(
    HttpClient http,
    IOptions<WxOptions> options,
    WxTokenCache tokens,
    IHostEnvironment env,
    ILogger<WxApiClient> logger) : IWxApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WxOptions _opt = options.Value;

    public async Task<WxSession> Code2SessionAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.AppSecret))
        {
            // 与 JWT__SECRET 同一口径：只有开发环境允许降级，生产必须拒绝而不是发一个固定身份
            if (!env.IsDevelopment())
            {
                logger.LogError("WX__SECRET 未配置：非开发环境拒绝登录降级（固定 openid 会让所有用户共用一个身份）");
                throw new InvalidOperationException(
                    "WX__SECRET 未配置：非开发环境不允许用固定 openid 降级登录");
            }

            logger.LogWarning("WX__SECRET 未配置，code2session 走本地开发模式（仅开发环境，openid={Openid}）", _opt.DevOpenid);
            return new WxSession(_opt.DevOpenid, null, null);
        }

        var url = $"{_opt.ApiBase}/sns/jscode2session?appid={_opt.AppId}&secret={_opt.AppSecret}&js_code={Uri.EscapeDataString(code)}&grant_type=authorization_code";
        var resp = await http.GetFromJsonAsync<Code2SessionResp>(url, Json, ct);
        if (resp is null || string.IsNullOrEmpty(resp.Openid))
            throw new ValidationException("微信登录失败：" + (resp?.ErrMsg ?? "no openid"));

        return new WxSession(resp.Openid, resp.Unionid, resp.SessionKey);
    }

    /// <summary>access_token 由单例 WxTokenCache 统一缓存（提前 5 分钟换新 + 刷新失败沿用旧 token）。</summary>
    private Task<string?> GetAccessTokenAsync(CancellationToken ct) => tokens.GetAsync(http, ct);

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

    private sealed class SendResp
    {
        [JsonPropertyName("errcode")] public int ErrCode { get; set; }
        [JsonPropertyName("errmsg")] public string? ErrMsg { get; set; }
    }
}
