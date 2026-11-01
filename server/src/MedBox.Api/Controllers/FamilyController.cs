using MedBox.Api.Common;
using MedBox.Api.Contracts;
using MedBox.Application.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedBox.Api.Controllers;

[ApiController]
[Route("api/family")]
[Authorize]
public sealed class FamilyController(
    FamilyInfoHandler info,
    FamilyListHandler list,
    FamilyCreateHandler create,
    FamilyJoinHandler join,
    FamilyUpdateRoleHandler updateRole,
    FamilyLeaveHandler leave,
    FamilyResetInviteHandler resetInvite) : ControllerBase
{
    [HttpGet("info")]
    public async Task<ApiResponse> Info([FromQuery] string fid, CancellationToken ct)
        => ApiResponse.Ok(await info.HandleAsync(fid, User.OpenId(), ct));

    [HttpGet("list")]
    public async Task<ApiResponse> List(CancellationToken ct)
        => ApiResponse.Ok(await list.HandleAsync(User.OpenId(), ct));

    [HttpPost("create")]
    public async Task<ApiResponse> Create([FromBody] FamilyCreateRequest req, CancellationToken ct)
        => ApiResponse.Ok(await create.HandleAsync(req.Name, User.OpenId(), ct));

    /// <summary>被邀请人落地：patient 只是入参意图，出参 role 恒为 admin|member|elder。</summary>
    [HttpPost("join")]
    public async Task<ApiResponse> Join([FromBody] FamilyJoinRequest req, CancellationToken ct)
        => ApiResponse.Ok(await join.HandleAsync(req.Fid, req.Ivt, req.Role ?? "guardian", req.Uid, User.OpenId(), info, ct));

    [HttpPost("update_role")]
    public async Task<ApiResponse> UpdateRole([FromBody] FamilyUpdateRoleRequest req, CancellationToken ct)
        => ApiResponse.Ok(await updateRole.HandleAsync(req.Fid, User.OpenId(), req.Openid, req.Role, ct));

    [HttpPost("leave")]
    public async Task<ApiResponse> Leave([FromBody] FamilyFidRequest req, CancellationToken ct)
        => ApiResponse.Ok(await leave.HandleAsync(req.Fid, User.OpenId(), ct));

    [HttpPost("reset_invite")]
    public async Task<ApiResponse> ResetInvite([FromBody] FamilyFidRequest req, CancellationToken ct)
        => ApiResponse.Ok(await resetInvite.HandleAsync(req.Fid, User.OpenId(), ct));
}
