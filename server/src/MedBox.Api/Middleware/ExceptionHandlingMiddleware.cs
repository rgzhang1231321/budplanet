using MedBox.Api.Common;
using MedBox.Domain;

namespace MedBox.Api.Middleware;

/// <summary>
/// 异常 → {code,data,msg} 的唯一映射点：
/// NotMember 1002 / Forbidden 1003 / Quota 1004 / Validation 1005 / 其余 1009 + 结构化日志。
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (MedBoxException ex)
        {
            // 业务异常用 Warning：它不是系统故障，但"用户点了没反应""名额不够"这类问题
            // 恰恰都从这里出来。用 Information 会被启动日志淹没，查库时根本翻不到。
            logger.LogWarning("业务异常 {Path} code={Code} msg={Msg}", ctx.Request.Path, ex.Code, ex.Message);
            await WriteAsync(ctx, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "未处理异常 {Path}", ctx.Request.Path);
            await WriteAsync(ctx, 1009, "服务异常，请稍后再试");
        }
    }

    private static async Task WriteAsync(HttpContext ctx, int code, string msg)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.Clear();
        ctx.Response.StatusCode = 200; // 封套优先：HTTP 层不区分业务错误，避免前端把 4xx 当网络错误
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsJsonAsync(ApiResponse.Fail(code, msg));
    }
}
