using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MedBox.Integration.Tests.TestSupport;

/// <summary>
/// 发 HTTP 请求的小封装：自动带 Bearer token，并把响应体解析成 JsonElement。
/// 刻意保留 HTTP 状态码 —— 封套的一致性要求"业务错误也回 200"，
/// 所以每条断言都要同时看 StatusCode 和 body.code。
/// </summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly Func<string, string> _mintToken;

    public ApiClient(HttpClient http, Func<string, string> mintToken)
    {
        _http = http;
        _mintToken = mintToken;
    }

    public async Task<(System.Net.HttpStatusCode Status, JsonElement Body)> GetAsync(
        string route, string openid)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, route);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mintToken(openid));
        return await SendAsync(req);
    }

    public async Task<(System.Net.HttpStatusCode Status, JsonElement Body)> PostAsync(
        string route, object? payload, string openid)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, route);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mintToken(openid));
        if (payload is not null)
        {
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        return await SendAsync(req);
    }

    private async Task<(System.Net.HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpRequestMessage req)
    {
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        JsonElement body;
        try
        {
            body = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"响应不是 JSON（HTTP {(int)resp.StatusCode}）：{text}", ex);
        }

        return (resp.StatusCode, body);
    }
}
