using System.Text;
using MedBox.Api.Common;
using MedBox.Api.Contracts;
using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using MedBox.Application.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedBox.Api.Controllers;

[ApiController]
[Route("api/user")]
public sealed class UserController(
    LoginHandler login,
    UserAgreeHandler agree,
    ExportHandler export,
    EraseHandler erase,
    IExportSigner signer) : ControllerBase
{
    /// <summary>冷启必调。入参 {code}（自建后端必须 code2session 换 openid）。</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ApiResponse> Login([FromBody] LoginRequest req, CancellationToken ct)
        => ApiResponse.Ok(await login.HandleAsync(req.Code, ct));

    [HttpPost("agree_privacy")]
    [Authorize]
    public async Task<ApiResponse> AgreePrivacy(CancellationToken ct)
        => ApiResponse.Ok(await agree.HandleAsync("privacy", User.OpenId(), ct));

    [HttpPost("agree_disclaimer")]
    [Authorize]
    public async Task<ApiResponse> AgreeDisclaimer(CancellationToken ct)
        => ApiResponse.Ok(await agree.HandleAsync("disclaimer", User.OpenId(), ct));

    /// <summary>
    /// 合规必须有（PRD 8.4）：导出本人全部数据，返回 24h 临时链接。
    /// fileUrl 用请求的 Host 拼绝对地址 —— 前端拿到就能直接 downloadFile，不用自己拼域名。
    /// </summary>
    [HttpPost("export")]
    [Authorize]
    public async Task<ApiResponse> ExportData(CancellationToken ct)
    {
        var ticket = await export.HandleAsync(User.OpenId(), ct);
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return ApiResponse.Ok(new { fileUrl = baseUrl + ticket.Path, expireAt = ticket.ExpireAt });
    }

    /// <summary>
    /// 预签名下载地址：**匿名**访问，凭证是 URL 里的签名 token（自带 openid + 24h 过期）。
    /// 这样浏览器/下载器直接打得开，不需要带 Authorization 头。
    /// </summary>
    [HttpGet("export/download")]
    [AllowAnonymous]
    public async Task<IActionResult> Download([FromQuery] string? token, CancellationToken ct)
    {
        if (!signer.TryVerify(token, out var openid, out _))
            return Unauthorized(new { code = 1001, msg = "导出链接已失效，请重新导出" });

        var json = await export.BuildJsonAsync(openid, ct);
        var fileName = $"medbox-export-{clockStamp()}.json";
        return File(Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8", fileName);
    }

    /// <summary>注销：必须有 confirm:true。软删个人数据、退出所有家庭、解绑但保留他人的服药记录。</summary>
    [HttpPost("erase")]
    [Authorize]
    public async Task<ApiResponse> EraseData([FromBody] UserEraseRequest req, CancellationToken ct)
        => ApiResponse.Ok(await erase.HandleAsync(req.Confirm, User.OpenId(), ct));

    private static string clockStamp()
        => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd-HHmm");
}
