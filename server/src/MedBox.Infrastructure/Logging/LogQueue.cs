using System.Threading.Channels;

namespace MedBox.Infrastructure.Logging;

/// <summary>
/// 日志队列：ILogger 只做入队（非阻塞），真正写库在 DbLogWriter 里批量做。
/// 为什么不在 Log 调用里直接写库：一次请求里会打十几条日志，同步写库会把接口 RT 拖成数据库 RT，
/// 而且数据库一抖就连带业务一起挂——日志不能反过来影响业务。
/// 队列满时丢新日志并计数（DropWrite），由 Writer 定期把丢弃数打到控制台。
/// </summary>
public sealed class LogQueue(int capacity)
{
    private readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(Math.Max(256, capacity))
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    private long _dropped;

    public void Enqueue(LogEntry entry)
    {
        if (!_channel.Writer.TryWrite(entry)) Interlocked.Increment(ref _dropped);
    }

    public ChannelReader<LogEntry> Reader => _channel.Reader;

    /// <summary>取走并清零"因队列满被丢弃"的条数。</summary>
    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);
}
