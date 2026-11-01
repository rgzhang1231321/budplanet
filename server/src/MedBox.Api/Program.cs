using System.Security.Cryptography;
using System.Text;
using MedBox.Api.Common;
using MedBox.Api.Middleware;
using MedBox.Infrastructure;
using MedBox.Infrastructure.Logging;
using MedBox.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ── 配置：JWT 秘钥只从环境变量 / user-secrets 来 ─────────────────
var jwtSecret = builder.Configuration["JWT__SECRET"] ?? builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    if (builder.Environment.IsDevelopment())
    {
        // 本地开发：进程内随机秘钥（重启即失效，免得把测试 token 当成长期凭证）
        jwtSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        Console.WriteLine("[WARN] JWT__SECRET 未配置，已生成本次进程专用的临时秘钥（重启后旧 token 全部失效）");
    }
    else
    {
        throw new InvalidOperationException("JWT__SECRET 未配置：生产环境必须通过环境变量注入（至少 32 字符）");
    }
}

builder.Services.Configure<JwtOptions>(o =>
{
    builder.Configuration.GetSection("Jwt").Bind(o);
    o.Secret = jwtSecret!;
});

builder.Services.AddMedBoxInfrastructure(builder.Configuration);
builder.Services.AddMedBoxApplication();

// ── 日志落库：ILogger → LogQueue（非阻塞）→ DbLogWriter（批量）→ app_logs ──────
// 放在这里而不是 Infrastructure 里，是因为 AddProvider 需要 provider 实例（Build 之前）。
// HttpContextAccessor 内部用静态 AsyncLocal，所以这里 new 的实例与容器里的是同一份上下文。
var dbLogOptions = builder.Configuration.GetSection("Logging:Db").Get<DbLogOptions>() ?? new DbLogOptions();
var httpAccessor = new HttpContextAccessor();
var logQueue = new LogQueue(dbLogOptions.MaxQueue);
builder.Services.AddSingleton(dbLogOptions);
builder.Services.AddSingleton(logQueue);
builder.Services.AddSingleton<IHttpContextAccessor>(httpAccessor);
builder.Services.AddSingleton<ClientLogLimiter>();
builder.Services.AddHostedService<DbLogWriter>();
builder.Logging.AddProvider(new DbLoggerProvider(logQueue, dbLogOptions, httpAccessor));

builder.Services.AddControllers().AddJsonOptions(o =>
{
    // 契约字段名逐字下发（DTO 已用 JsonPropertyName 写死），不做驼峰转换
    o.JsonSerializerOptions.PropertyNamingPolicy = null;
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "medbox",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "medbox-mp",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret!)),
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        // 未登录 / 过期 → 仍走封套 code=1001，前端据此静默续期
        o.Events = new JwtBearerEvents
        {
            OnChallenge = async ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(ApiResponse.Fail(1001, "未登录或登录过期"));
            },
        };
    });

builder.Services.AddAuthorization();

// 模型绑定失败也要走封套（默认是 400 ProblemDetails，前端会当成网络异常）
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(o =>
{
    o.InvalidModelStateResponseFactory = ctx =>
    {
        var msg = ctx.ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .FirstOrDefault() ?? "参数错误";
        return new Microsoft.AspNetCore.Mvc.OkObjectResult(ApiResponse.Fail(1005, msg));
    };
});

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// 健康检查：只回 ok，不泄露任何配置
app.MapGet("/health", () => Results.Json(new { ok = true }));

app.Run();

public partial class Program;
