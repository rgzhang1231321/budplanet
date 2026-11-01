namespace MedBox.Application.Abstractions;

/// <summary>
/// 数据导出下载链接的签名器（契约：`user.export` 返回临时链接 24h）。
/// 用无状态 HMAC：token 自身携带 openid 与过期时间，不落库、不怕进程重启，
/// 24 小时后自然失效 —— 比"写一张临时表再定时清理"少一个会漏清理的定时任务。
/// </summary>
public interface IExportSigner
{
    string Sign(string openid, long expireAt);

    bool TryVerify(string? token, out string openid, out long expireAt);
}
