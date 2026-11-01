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

    /// <summary>
    /// UGC 内容安全校验总开关（默认开）。关掉的场景：微信接口限流/故障期间要保住"能存药"这条主链路。
    /// 走配置不走常量：出事时希望改配置重启就能关，而不是发一版。
    /// </summary>
    public bool MsgSecCheckEnabled { get; set; } = true;
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

    /// <summary>msgSecCheck 命中违禁的 errcode（老版本返回形态）。</summary>
    private const int SecCheckRiskyErrCode = 87014;

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

    /// <summary>
    /// 内容安全校验（药名/备注/家庭名/服药人称呼）。
    /// 失败一律放行 + Warning：微信抖动或 access_token 拿不到时若拒写，用户当天就存不了药，
    /// 代价比"一条备注待复核"大得多 —— 这条链路的第一优先级是药能进库。
    /// </summary>
    public async Task<WxSecVerdict> MsgSecCheckAsync(string content, string openid, CancellationToken ct = default)
    {
        if (!_opt.MsgSecCheckEnabled || string.IsNullOrWhiteSpace(content)) return WxSecVerdict.Pass;

        var token = await GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
        {
            logger.LogWarning("内容安全校验跳过：拿不到 access_token（WX__SECRET 未配置），按放行处理");
            return WxSecVerdict.Pass;
        }

        // version=2 + scene=2（评论/资料场景）走新版 suggest 判定；openid 必传，微信按用户维度做风控
        var body = new { content, version = 2, scene = 2, openid };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.ApiBase}/wxa/msg_sec_check?access_token={token}")
            {
                Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
            };
            using var resp = await http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<SecCheckResp>(text, Json);

            if (result is null)
            {
                logger.LogWarning("内容安全校验返回空响应，按放行处理");
                return WxSecVerdict.Pass;
            }

            // 老版本直接把违规当错误码返回（87014）；新版已改为 errcode=0 + result.suggest，两种都认
            if (result.ErrCode == SecCheckRiskyErrCode) return WxSecVerdict.Risky;

            if (result.ErrCode != 0)
            {
                logger.LogWarning("内容安全校验失败 errcode={Code} msg={Msg}，按放行处理", result.ErrCode, result.ErrMsg);
                return WxSecVerdict.Pass;
            }

            // suggest 只有 risky 拒；review（建议人工复核）不挡用户
            return string.Equals(result.Result?.Suggest, "risky", StringComparison.OrdinalIgnoreCase)
                ? WxSecVerdict.Risky
                : WxSecVerdict.Pass;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "内容安全校验异常，按放行处理");
            return WxSecVerdict.Pass;
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

    private sealed class SecCheckResp
    {
        [JsonPropertyName("errcode")] public int ErrCode { get; set; }
        [JsonPropertyName("errmsg")] public string? ErrMsg { get; set; }
        [JsonPropertyName("result")] public SecCheckResult? Result { get; set; }
    }

    private sealed class SecCheckResult
    {
        /// <summary>pass / review / risky。</summary>
        [JsonPropertyName("suggest")] public string? Suggest { get; set; }
        [JsonPropertyName("label")] public int Label { get; set; }
    }
}
