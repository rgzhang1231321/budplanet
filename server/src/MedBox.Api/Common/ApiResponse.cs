using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace MedBox.Api.Common;

/// <summary>统一响应封套 {code,data,msg}。所有读接口的 data 必带 server_ts（用例里写，不用中间件猜）。</summary>
public sealed class ApiResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("data")] public object? Data { get; set; }
    [JsonPropertyName("msg")] public string Msg { get; set; } = "ok";

    public static ApiResponse Ok(object? data) => new() { Code = 0, Data = data, Msg = "ok" };
    public static ApiResponse Fail(int code, string msg) => new() { Code = code, Data = new { }, Msg = msg };
}

public static class ClaimsExtensions
{
    /// <summary>openid 只从 JWT sub 取；请求体里出现 openid 一律忽略。</summary>
    public static string OpenId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    public static string? Query(this ControllerBase c, string name) =>
        c.Request.Query.TryGetValue(name, out var v) ? v.ToString() : null;
}
