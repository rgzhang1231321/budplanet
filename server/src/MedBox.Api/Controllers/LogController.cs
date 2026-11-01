using System.Text.Json;
using MedBox.Api.Common;
using MedBox.Infrastructure.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MedBox.Api.Controllers;

/// <summary>
/// 小程序端日志上报。与后端 ILogger 落同一张 app_logs（source=client），
/// 这样"按钮点了没反应 / 页面白屏"这类问题不用靠用户口述控制台，直接查库。
///
/// 为什么匿名：崩溃常常发生在登录之前（启动页、邀请落地页），要求 token 会把最有价值的那批日志挡掉。
/// 代价是被刷库，所以有 IP 分钟级限流 + 字段长度硬上限。
/// </summary>
[ApiController]
[Route("api/log")]
[AllowAnonymous]
public sealed class LogController(
    LogQueue queue,
    DbLogOptions opts,
    ClientLogLimiter limiter) : ControllerBase
{
    /// <summary>单次上报的原始 body 上限，防止有人塞一个 10MB 的 stack。</summary>
    private const int MaxBodyChars = 16 * 1024;

    [HttpPost("client")]
    [RequestSizeLimit(MaxBodyChars)]
    public ApiResponse Client([FromBody] ClientLogRequest? req)
    {
        if (req is null) return ApiResponse.Ok(new { ok = false, why = "empty" });

        if (!opts.Enabled) return ApiResponse.Ok(new { ok = false, why = "disabled" });

        // 限流键：优先 X-Forwarded-For 首个地址，否则连接远端地址；都取不到就用固定键（本机场景）
        var ip = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(ip)) ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!limiter.Allow(ip, perMinute: 60))
            return ApiResponse.Ok(new { ok = false, why = "rate_limited" });

        var level = ParseLevel(req.Level);
        var ts = req.Ts is > 0 and var t && t < 4_000_000_000_000 ? req.Ts!.Value : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var props = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(req.Scene)) props["scene"] = Clip(req.Scene, 64);
        if (!string.IsNullOrWhiteSpace(req.AppVersion)) props["app_version"] = Clip(req.AppVersion, 32);
        if (!string.IsNullOrWhiteSpace(req.System)) props["system"] = Clip(req.System, 128);
        if (req.Extra is not null && req.Extra.Count > 0)
        {
            // 只收标量，避免嵌套对象把 jsonb 撑大；键名与值都截断。
            // 值必须过一遍脱敏 —— props 是自由字段，客户端可能把手机号、token 顺手塞进来。
            foreach (var kv in req.Extra.Take(12))
                props["x_" + Clip(kv.Key, 32)] = LogSanitizer.Sanitize(Clip(kv.Value?.ToString(), 200), 200);
        }

        string? propsJson = props.Count == 0 ? null : JsonSerializer.Serialize(props);

        var page = Clip(req.Page, 200);

        // 交给同一套 provider 读 page（DbLoggerProvider 会取 HttpContext.Items["log_page"]）
        if (page is not null) HttpContext.Items["log_page"] = page;

        queue.Enqueue(new LogEntry(
            TsMs: ts,
            Level: level.ToString(),
            LevelNum: (short)level,
            Source: "client",
            Category: "client",
            EventId: Clip(req.Scene, 64),
            Message: LogSanitizer.Sanitize(Clip(req.Message, opts.MaxMessageChars), opts.MaxMessageChars),
            // 客户端把堆栈放 stack，落到 exception 列：查库时按 exception 搜就对了
            Exception: LogSanitizer.Sanitize(Clip(req.Stack, opts.MaxExceptionChars), opts.MaxExceptionChars),
            PropsJson: propsJson,
            TraceId: HttpContext.TraceIdentifier,
            HttpMethod: HttpContext.Request.Method,
            HttpPath: HttpContext.Request.Path.Value,
            StatusCode: 200,
            Openid: User.OpenId() is { Length: > 0 } oid ? oid : null,
            Page: page));

        return ApiResponse.Ok(new { ok = true });
    }

    /// <summary>客户端传小写（error/warn/info），也接受 .NET 枚举名；未知一律当 Information。</summary>
    private static LogLevel ParseLevel(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "critical" or "fatal" => LogLevel.Critical,
        "error" => LogLevel.Error,
        "warn" or "warning" => LogLevel.Warning,
        "debug" => LogLevel.Debug,
        "trace" => LogLevel.Trace,
        _ => LogLevel.Information,
    };

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Length <= max ? s : s[..max] + "…";
    }
}

/// <summary>客户端上报的原始载荷。字段名用小写下划线，与小程序侧一致。</summary>
public sealed class ClientLogRequest
{
    public string? Level { get; set; }
    public string? Message { get; set; }
    public string? Stack { get; set; }
    public string? Page { get; set; }
    public string? Scene { get; set; }
    public string? AppVersion { get; set; }
    public string? System { get; set; }
    public long? Ts { get; set; }
    public Dictionary<string, object?>? Extra { get; set; }
}
