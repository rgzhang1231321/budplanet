using MedBox.Api.Common;
using MedBox.Application.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedBox.Api.Controllers;

[ApiController]
[Route("api/schedule")]
[Authorize]
public sealed class ScheduleController(TodayHandler today, WeekHandler week) : ControllerBase
{
    /// <summary>老人端与子女端同一接口；老人端在服务端按 selfUids 过滤。</summary>
    [HttpGet("today")]
    public async Task<ApiResponse> Today([FromQuery] string fid, [FromQuery] string? date, CancellationToken ct)
        => ApiResponse.Ok(await today.HandleAsync(fid, date ?? "", User.OpenId(), ct));

    [HttpGet("week")]
    public async Task<ApiResponse> Week([FromQuery] string fid, [FromQuery] string? weekStart, CancellationToken ct)
        => ApiResponse.Ok(await week.HandleAsync(fid, weekStart, User.OpenId(), ct));
}
