using System.Collections.Concurrent;

namespace MedBox.Infrastructure.Logging;

/// <summary>
/// 客户端日志上报的轻量限流。这个接口必须匿名（启动阶段崩了也要能报），
/// 匿名 + 写库 = 天然的刷库入口，所以在入口挡住：每个 IP 每分钟固定额度，超了直接丢。
/// 进程内计数即可，不做分布式——多实例时每实例各限一份，量级足够。
/// </summary>
public sealed class ClientLogLimiter
{
    private readonly ConcurrentDictionary<string, (long Window, int Count)> _map = new();

    public bool Allow(string key, int perMinute)
    {
        var window = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60_000;
        var entry = _map.AddOrUpdate(
            key,
            _ => (window, 1),
            (_, old) => old.Window == window ? (window, old.Count + 1) : (window, 1));

        // 简单的防膨胀：超过 5000 个 key 时清掉上一个时间窗的全部记录
        if (_map.Count > 5000)
        {
            foreach (var kv in _map)
            {
                if (kv.Value.Window < window) _map.TryRemove(kv.Key, out _);
            }
        }

        return entry.Count <= perMinute;
    }
}
