using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MedBox.Infrastructure.Wx;

/// <summary>
/// access_token 的**进程级单例**缓存。必须单例：WxApiClient 是 typed HttpClient（每次注入都是新实例），
/// 缓存挂在它身上等于没缓存 —— 每次推送都重新取 token，一天 8600 次会触发微信侧限流，
/// 之后所有提醒静默停发（用户以为"今天没药"，实际是账号被限流）。
/// 取 token 用 SemaphoreSlim 串行（防击穿），提前 5 分钟换新；刷新失败沿用旧 token 到真过期。
/// 水平扩展多实例时改 Redis（单实例够用，不引入 Redis）。
/// </summary>
public sealed class WxTokenCache(IOptions<WxOptions> options, TimeProvider time, ILogger<WxTokenCache> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>微信 expires_in 默认 7200s，提前 5 分钟过期：卡在边界上拿过期 token 发消息等于白发。</summary>
    private const int SafetyMarginSeconds = 300;

    /// <summary>刷新失败后的退避：别让每 60s 的 tick 都去打一次微信接口，把失败放大成限流。</summary>
    private const long RefreshFailureBackoffMs = 30_000L;

    private readonly WxOptions _opt = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    /// <summary>到这个时刻就该换新 token（= 微信给的过期时间 - 安全边界）。</summary>
    private long _refreshAtMs;
    /// <summary>旧 token 真正失效的时刻：刷新失败时用它判断"还能不能继续用旧的"。</summary>
    private long _hardExpireAtMs;

    public async Task<string?> GetAsync(HttpClient http, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.AppSecret)) return null;

        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var cached = Volatile.Read(ref _token);
        if (cached is not null && now < Volatile.Read(ref _refreshAtMs)) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            now = time.GetUtcNow().ToUnixTimeMilliseconds();
            cached = _token;
            if (cached is not null && now < _refreshAtMs) return cached; // 双检：别的线程刚刷完，别重复打微信

            var fresh = await FetchAsync(http, ct);
            if (fresh is null)
            {
                // 刷新失败不许让整条提醒链路停摆：旧 token 没真过期就继续用
                if (cached is not null && now < _hardExpireAtMs)
                {
                    _refreshAtMs = now + RefreshFailureBackoffMs;
                    logger.LogWarning(
                        "access_token 刷新失败，沿用旧 token（{BackoffSeconds}s 后重试，旧 token 还剩 {LeftSeconds}s）",
                        RefreshFailureBackoffMs / 1000, (_hardExpireAtMs - now) / 1000);
                    return cached;
                }
                return null;
            }

            var (token, expiresInSeconds) = fresh.Value;
            _token = token;
            _hardExpireAtMs = now + expiresInSeconds * 1000L;
            _refreshAtMs = now + Math.Max(60, expiresInSeconds - SafetyMarginSeconds) * 1000L;
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string Token, int ExpiresIn)?> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var url = $"{_opt.ApiBase}/cgi-bin/token?grant_type=client_credential&appid={_opt.AppId}&secret={_opt.AppSecret}";
        TokenResp? resp;
        try
        {
            resp = await http.GetFromJsonAsync<TokenResp>(url, Json, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // 停机 / 请求取消：不是"拿不到 token"，不要吞
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取 access_token 网络异常");
            return null;
        }

        if (resp is null || string.IsNullOrEmpty(resp.AccessToken))
        {
            logger.LogError("获取 access_token 失败：{ErrCode} {ErrMsg}", resp?.ErrCode, resp?.ErrMsg);
            return null;
        }

        return (resp.AccessToken, resp.ExpiresIn > 0 ? resp.ExpiresIn : 7200);
    }

    private sealed class TokenResp
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("errcode")] public int ErrCode { get; set; }
        [JsonPropertyName("errmsg")] public string? ErrMsg { get; set; }
    }
}
