using System.Text.Json;
using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MedBox.Application.Services;

/// <summary>
/// app_config 读取：config_key='runtime' 存整块 camelCase，直接反序列化，不做 key 转换。
/// 必须是单例（DI 里注册为 Singleton）：缓存字段挂在实例上，Scoped 注册等于每请求一个新实例，
/// 那句"60s 进程内缓存"就永远不命中，等于每次登录都查库。
/// 因为要吃 Scoped 的 IAppDbContext，这里只持有 <see cref="IServiceScopeFactory"/>，读库时现开一个作用域。
/// </summary>
public sealed class ConfigReader(IServiceScopeFactory scopes)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>缓存有效期。广告开关要能不发版生效，但也不必每次登录查库。</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppConfigDto? _cached;
    private long _expiresAtMs;

    public async Task<AppConfigDto> GetAsync(CancellationToken ct = default)
    {
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && Environment.TickCount64 < Volatile.Read(ref _expiresAtMs)) return cached;

        // 过期后串行刷新：并发请求只让第一个查库，其余等它填好缓存直接读
        await _gate.WaitAsync(ct);
        try
        {
            cached = _cached;
            if (cached is not null && Environment.TickCount64 < _expiresAtMs) return cached;

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

            var row = await db.AppConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.ConfigKey == "runtime", ct);
            var cfg = row is null || string.IsNullOrWhiteSpace(row.ConfigValue)
                ? new AppConfigDto()
                : JsonSerializer.Deserialize<AppConfigDto>(row.ConfigValue, Json) ?? new AppConfigDto();

            // 先写过期时刻再发布内容：反过来的话，读线程可能看到新对象配旧过期时间
            Volatile.Write(ref _expiresAtMs, Environment.TickCount64 + (long)Ttl.TotalMilliseconds);
            Volatile.Write(ref _cached, cfg);
            return cfg;
        }
        finally
        {
            _gate.Release();
        }
    }
}
