using System.Text.Json.Serialization;
using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MedBox.Api.Common;

namespace MedBox.Api.Controllers;

public sealed class AdUnlockRequest
{
    [JsonPropertyName("product_id")] public string ProductId { get; set; } = "";
    [JsonPropertyName("ad_trace")] public string AdTrace { get; set; } = "";
}

public sealed class AdUnlockResponse
{
    [JsonPropertyName("granted")] public bool Granted { get; set; }
    [JsonPropertyName("credits")] public int Credits { get; set; }
    [JsonPropertyName("expireAt")] public long ExpireAt { get; set; }
}

/// <summary>ad.unlock：激励视频看完后核销名额。幂等 + 30s 窗 + 日上限。</summary>
[ApiController]
[Route("api/ad")]
[Authorize]
public sealed class AdController(EntitlementService entitlements) : ControllerBase
{
    [HttpPost("unlock")]
    public async Task<ApiResponse> Unlock([FromBody] AdUnlockRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ProductId)) throw new ValidationException("缺少 product_id");
        var r = await entitlements.GrantByAdAsync(req.ProductId, req.AdTrace, User.OpenId(), ct);
        return ApiResponse.Ok(new AdUnlockResponse { Granted = r.Granted, Credits = r.Credits, ExpireAt = r.ExpireAt });
    }
}
