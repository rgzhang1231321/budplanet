using System.Text.Json;
using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.Services;

/// <summary>app_config 读取：config_key='runtime' 存整块 camelCase，直接反序列化，不做 key 转换。</summary>
public sealed class ConfigReader(IAppDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private AppConfigDto? _cached;
    private long _cachedAtMs;

    public async Task<AppConfigDto> GetAsync(CancellationToken ct = default)
    {
        // 60s 进程内缓存：广告开关要能不发版生效，但也不必每次登录查库
        if (_cached is not null && Environment.TickCount64 - _cachedAtMs < 60_000) return _cached;

        var row = await db.AppConfigs.FirstOrDefaultAsync(x => x.ConfigKey == "runtime", ct);
        AppConfigDto cfg;
        if (row is null || string.IsNullOrWhiteSpace(row.ConfigValue))
        {
            cfg = new AppConfigDto();
        }
        else
        {
            cfg = JsonSerializer.Deserialize<AppConfigDto>(row.ConfigValue, Json) ?? new AppConfigDto();
        }

        _cached = cfg;
        _cachedAtMs = Environment.TickCount64;
        return cfg;
    }
}
