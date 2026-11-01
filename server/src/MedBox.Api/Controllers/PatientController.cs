using MedBox.Api.Common;
using MedBox.Api.Contracts;
using MedBox.Application.Abstractions;
using MedBox.Application.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedBox.Api.Controllers;

[ApiController]
[Route("api/patient")]
[Authorize]
public sealed class PatientController(
    PatientListHandler list,
    PatientCreateHandler create,
    PatientUpdateHandler update,
    PatientArchiveHandler archive,
    IMemberGuard guard) : ControllerBase
{
    [HttpGet("list")]
    public async Task<ApiResponse> List([FromQuery] string fid, CancellationToken ct)
        => ApiResponse.Ok(await list.HandleAsync(fid, User.OpenId(), guard, ct));

    [HttpPost("create")]
    public async Task<ApiResponse> Create([FromBody] PatientCreateRequest req, CancellationToken ct)
        => ApiResponse.Ok(await create.HandleAsync(req.Fid, User.OpenId(), req.Name, req.Alias, ct));

    [HttpPost("update")]
    public async Task<ApiResponse> Update([FromBody] PatientUpdateRequest req, CancellationToken ct)
        => ApiResponse.Ok(await update.HandleAsync(req.Fid, User.OpenId(), req.Uid, req.Name, req.Alias, req.Avatar, ct));

    [HttpPost("archive")]
    public async Task<ApiResponse> Archive([FromBody] PatientArchiveRequest req, CancellationToken ct)
        => ApiResponse.Ok(await archive.HandleAsync(req.Fid, User.OpenId(), req.Uid, ct));
}
