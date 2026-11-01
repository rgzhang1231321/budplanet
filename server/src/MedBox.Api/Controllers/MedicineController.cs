using MedBox.Api.Common;
using MedBox.Api.Contracts;
using MedBox.Application.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedBox.Api.Controllers;

[ApiController]
[Route("api/medicine")]
public sealed class MedicineController(
    MedicineListHandler list,
    CommonMedsHandler common,
    MedicineCreateHandler create,
    MedicineUpdateHandler update,
    MedicineArchiveHandler archive) : ControllerBase
{
    [HttpGet("list")]
    [Authorize]
    public async Task<ApiResponse> List([FromQuery] string fid, [FromQuery] string? uid, CancellationToken ct)
        => ApiResponse.Ok(await list.HandleAsync(fid, uid, User.OpenId(), ct));

    /// <summary>公开只读：常用药预设（替代云开发时代的前端直读集合）。</summary>
    [HttpGet("common")]
    [AllowAnonymous]
    public async Task<ApiResponse> Common(CancellationToken ct)
        => ApiResponse.Ok(await common.HandleAsync(ct));

    [HttpPost("create")]
    [Authorize]
    public async Task<ApiResponse> Create([FromBody] MedicineCreateRequest req, CancellationToken ct)
        => ApiResponse.Ok(await create.HandleAsync(req.Fid, User.OpenId(), req.Uid, req.Name, req.Spec, req.DefaultDose, req.Note, ct));

    [HttpPost("update")]
    [Authorize]
    public async Task<ApiResponse> Update([FromBody] MedicineCreateRequest req, CancellationToken ct)
        => ApiResponse.Ok(await update.HandleAsync(req.Fid, User.OpenId(), req.Uid, req.Mid ?? "", req.Name, req.Spec, req.DefaultDose, req.Note, ct));

    [HttpPost("archive")]
    [Authorize]
    public async Task<ApiResponse> Archive([FromBody] MedicineArchiveRequest req, CancellationToken ct)
        => ApiResponse.Ok(await archive.HandleAsync(req.Fid, User.OpenId(), req.Mid, ct));
}
