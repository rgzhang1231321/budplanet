using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedBox.Infrastructure.Logging;

/// <summary>
/// 把 ILogger 的日志接进 LogQueue，再由 DbLogWriter 批量写 app_logs。
/// 只做入队，不碰数据库：数据库慢或挂掉时，记日志本身不能变成业务失败点。
/// 注意：EF 与 ASP.NET 框架分类默认被排除（见 DbLogOptions.ExcludeCategories）——
/// 一是太吵，二是 EF 的 SQL 日志会把剂量、药名原样带进库。
/// </summary>
[ProviderAlias("DbLog")]
public sealed class DbLoggerProvider : ILoggerProvider
{
    private readonly LogQueue _queue;
    private readonly DbLogOptions _opt;
    private readonly LogLevel _min;
    private readonly IHttpContextAccessor _http;

    public DbLoggerProvider(LogQueue queue, DbLogOptions opt, IHttpContextAccessor http)
    {
        _queue = queue;
        _opt = opt;
        _min = opt.ParsedMinLevel;
        _http = http;
    }

    public ILogger CreateLogger(string categoryName)
    {
        if (!_opt.Enabled) return NullLogger.Instance;
        if (_min == LogLevel.None) return NullLogger.Instance;
        foreach (var prefix in _opt.ExcludeCategories)
        {
            if (categoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return NullLogger.Instance;
        }
        return new DbLogger(this, categoryName);
    }

    internal bool IsEnabled(LogLevel level) => _opt.Enabled && level != LogLevel.None && level >= _min;

    internal void Write(string category, LogLevel level, EventId eventId, string message, Exception? ex, string? propsJson)
    {
        string? traceId = null, method = null, path = null, openid = null, page = null;
        int? status = null;

        try
        {
            var ctx = _http.HttpContext;
            if (ctx is not null)
            {
                traceId = ctx.TraceIdentifier;
                method = ctx.Request.Method;
                path = ctx.Request.Path.Value;
                status = ctx.Response.StatusCode;
                // JwtBearer 默认 MapInboundClaims=true，会把 sub 映射成 nameidentifier：
                // 两种都试，否则服务端日志里的 openid 永远是空的（很难发现，因为不影响功能）。
                openid = ctx.User?.FindFirst("sub")?.Value
                         ?? ctx.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                // 客户端上报时把页面路径塞进 Items，这里顺带带上
                if (ctx.Items.TryGetValue("log_page", out var pg)) page = pg as string;
            }
        }
        catch
        {
            // 取上下文失败不能影响记日志本身
        }

        _queue.Enqueue(new LogEntry(
            TsMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Level: level.ToString(),
            LevelNum: (short)level,
            Source: "server",
            Category: category,
            EventId: eventId.Name,
            Message: LogSanitizer.Sanitize(message, _opt.MaxMessageChars),
            Exception: ex is null ? null : LogSanitizer.Sanitize(ex.ToString(), _opt.MaxExceptionChars),
            PropsJson: propsJson,
            TraceId: traceId,
            HttpMethod: method,
            HttpPath: path,
            StatusCode: status,
            Openid: openid,
            Page: page));
    }

    public void Dispose()
    {
        // 队列由容器管理，这里没有需要释放的资源
    }

    private sealed class DbLogger(DbLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string message;
            try
            {
                message = formatter(state, exception);
            }
            catch
            {
                message = "(日志格式化失败)";
            }

            provider.Write(category, logLevel, eventId, message, exception, BuildProps(state));
        }

        /// <summary>把 logger 的结构化占位符（{DoseId} 之类）落成 jsonb，方便按字段检索。</summary>
        private static string? BuildProps<TState>(TState state)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> pairs) return null;

            Dictionary<string, object?>? dict = null;
            foreach (var kv in pairs)
            {
                if (kv.Key == "{OriginalFormat}") continue;
                dict ??= [];
                dict[kv.Key] = kv.Value switch
                {
                    null => null,
                    string s => LogSanitizer.Sanitize(s, 1000),
                    _ => kv.Value.ToString(),
                };
            }
            if (dict is null) return null;

            try
            {
                return JsonSerializer.Serialize(dict);
            }
            catch
            {
                return null;
            }
        }
    }
}
