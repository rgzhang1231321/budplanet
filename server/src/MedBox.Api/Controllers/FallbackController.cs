using MedBox.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace MedBox.Api.Controllers;

/// <summary>
/// 兜底：未实现的 action 也走封套 1005，绝不回 HTML 404
/// （前端 request 把非封套响应当成网络异常，会污染错误提示）。
/// </summary>
[ApiController]
public sealed class FallbackController : ControllerBase
{
    [Route("api/{domain}/{action}")]
    [AcceptVerbs("GET", "POST")]
    public ApiResponse Any(string domain, string action)
        => ApiResponse.Fail(1005, $"接口暂未实现：{domain}.{action}");
}
