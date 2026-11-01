namespace MedBox.Infrastructure.Logging;

/// <summary>最终连接串（已补齐口令）。日志写入要独立于 EF 连接，所以单独持有。</summary>
public sealed record MedBoxConnection(string Value);
