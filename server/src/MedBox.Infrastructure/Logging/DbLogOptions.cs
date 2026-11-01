namespace MedBox.Infrastructure.Logging;

/// <summary>
/// 日志落库配置（appsettings 的 Logging:Db 段）。
/// 默认值按"开发机够用、生产不炸库"取：Information 起、1 秒一批、最多 2 万条在队列里。
/// </summary>
public sealed class DbLogOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>最低落库级别（Trace/Debug/Information/Warning/Error/Critical/None）。</summary>
    public string MinLevel { get; set; } = "Information";

    /// <summary>一批最多写多少行。</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>队列积压上限。满了丢新日志并计数——宁可丢日志，不能撑爆内存。</summary>
    public int MaxQueue { get; set; } = 20000;

    /// <summary>保留天数，超期由 ArchiveWorker 分批删。</summary>
    public int RetainDays { get; set; } = 30;

    /// <summary>单条 message / exception 截断长度（防止一条巨大的堆栈把表撑爆）。</summary>
    public int MaxMessageChars { get; set; } = 4000;
    public int MaxExceptionChars { get; set; } = 8000;

    /// <summary>
    /// 不落库的 logger 分类前缀。默认排掉 EF 与 ASP.NET 框架日志：
    /// EF 的 SQL 日志会把剂量明细、药名一起写进去，既有隐私问题也吵。
    /// </summary>
    public string[] ExcludeCategories { get; set; } =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
    ];

    public Microsoft.Extensions.Logging.LogLevel ParsedMinLevel =>
        Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(MinLevel, ignoreCase: true, out var l)
            ? l
            : Microsoft.Extensions.Logging.LogLevel.Information;
}
