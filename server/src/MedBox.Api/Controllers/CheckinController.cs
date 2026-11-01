using MedBox.Api.Common;
using MedBox.Application.Contracts;
using MedBox.Application.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedBox.Api.Controllers;

[ApiController]
[Route("api/checkin")]
[Authorize]
public sealed class CheckinController(
    SubmitHandler submit,
    SnoozeHandler snooze,
    CorrectHandler correct) : ControllerBase
{
    /// <summary>天生批量（1–8 条），幂等键 pid_date_slot，部分成功合法。</summary>
    [HttpPost("submit")]
    public async Task<ApiResponse> Submit([FromBody] SubmitRequest req, CancellationToken ct)
        => ApiResponse.Ok(await submit.HandleAsync(req, User.OpenId(), ct));

    [HttpPost("snooze")]
    public async Task<ApiResponse> Snooze([FromBody] SnoozeRequest req, CancellationToken ct)
        => ApiResponse.Ok(await snooze.HandleAsync(req.DoseId, User.OpenId(), ct));

    /// <summary>仅 admin/member；elder 一律 1003（不依赖"前端没入口"）。</summary>
    [HttpPost("correct")]
    public async Task<ApiResponse> Correct([FromBody] CorrectRequest req, CancellationToken ct)
        => ApiResponse.Ok(await correct.HandleAsync(req.DoseId, req.ToStatus, req.Reason, User.OpenId(), ct));
}
