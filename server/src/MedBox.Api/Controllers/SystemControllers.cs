using MedBox.Api.Common;
using MedBox.Application.Contracts;
using MedBox.Application.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedBox.Api.Controllers;

[ApiController]
[Route("api/config")]
[Authorize]
public sealed class ConfigController(ConfigGetHandler get) : ControllerBase
{
    [HttpGet("get")]
    public async Task<ApiResponse> Get(CancellationToken ct) => ApiResponse.Ok(await get.HandleAsync(ct));
}

[ApiController]
[Route("api/notify")]
[Authorize]
public sealed class NotifyController(QuotaHandler quota, NotifyAckHandler ack) : ControllerBase
{
    [HttpGet("quota")]
    public async Task<ApiResponse> Quota(CancellationToken ct)
        => ApiResponse.Ok(await quota.HandleAsync(User.OpenId(), ct));

    /// <summary>必须在 requestSubscribeMessage 回调里立刻调；拒授权也要调一次。</summary>
    [HttpPost("ack")]
    public async Task<ApiResponse> Ack([FromBody] AckRequest req, CancellationToken ct)
        => ApiResponse.Ok(await ack.HandleAsync(req, User.OpenId(), ct));
}

[ApiController]
[Route("api/report")]
[Authorize]
public sealed class ReportController(ReportBatchHandler batch) : ControllerBase
{
    /// <summary>埋点攒批。参数在服务端过一遍隐私闸门，原文不落库、只留枚举分布。</summary>
    [HttpPost("batch")]
    public async Task<ApiResponse> Batch([FromBody] ReportBatchRequest req, CancellationToken ct)
        => ApiResponse.Ok(await batch.HandleAsync(req.Events, ct));
}
