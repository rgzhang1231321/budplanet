using MedBox.Api.Common;
using MedBox.Api.Contracts;
using MedBox.Application.Contracts;
using MedBox.Application.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedBox.Api.Controllers;

[ApiController]
[Route("api/plan")]
[Authorize]
public sealed class PlanController(PlanUpsertHandler upsert, PlanFinishHandler finish) : ControllerBase
{
    [HttpPost("create")]
    public Task<ApiResponse> Create([FromBody] PlanUpsertRequest req, CancellationToken ct) => Upsert(req, ct);

    [HttpPost("update")]
    public Task<ApiResponse> Update([FromBody] PlanUpsertRequest req, CancellationToken ct) => Upsert(req, ct);

    [HttpPost("finish")]
    public async Task<ApiResponse> Finish([FromBody] PlanFinishRequest req, CancellationToken ct)
        => ApiResponse.Ok(await finish.HandleAsync(req.PlanId, User.OpenId(), ct));

    private async Task<ApiResponse> Upsert(PlanUpsertRequest req, CancellationToken ct)
        => ApiResponse.Ok(await upsert.HandleAsync(req.Mid, req.Slots, req.CycleType, req.CycleDays, req.StartDate, User.OpenId(), ct));
}
