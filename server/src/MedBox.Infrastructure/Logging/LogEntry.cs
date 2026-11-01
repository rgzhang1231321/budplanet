namespace MedBox.Infrastructure.Logging;

/// <summary>一条待落库的日志。字段与 app_logs 一一对应，不含任何未脱敏的凭据。</summary>
public sealed record LogEntry(
    long TsMs,
    string Level,
    short LevelNum,
    string Source,
    string? Category,
    string? EventId,
    string? Message,
    string? Exception,
    string? PropsJson,
    string? TraceId,
    string? HttpMethod,
    string? HttpPath,
    int? StatusCode,
    string? Openid,
    string? Page);
