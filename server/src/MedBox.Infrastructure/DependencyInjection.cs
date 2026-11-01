using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Application.UseCases;
using MedBox.Infrastructure.Access;
using MedBox.Infrastructure.Persistence;
using MedBox.Infrastructure.Security;
using MedBox.Infrastructure.Wx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MedBox.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMedBoxInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("MedBox")
                 ?? throw new InvalidOperationException("缺少连接字符串 ConnectionStrings:MedBox");

        // 口令不进 appsettings（禁止提交密钥）：只从环境变量 PG__PASSWORD / user-secrets 补
        var pwd = config["PG__PASSWORD"] ?? config["PG:Password"];
        if (!string.IsNullOrWhiteSpace(pwd) && !cs.Contains("Password=", StringComparison.OrdinalIgnoreCase))
            cs += $";Password={pwd}";

        services.AddDbContext<MedBoxDbContext>(o => o.UseNpgsql(cs));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<MedBoxDbContext>());

        // 日志写入要绕开 EF（否则日志写日志会递归），单独持有一份最终连接串
        services.AddSingleton(new Logging.MedBoxConnection(cs));

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IClock, SystemClock>();

        services.Configure<WxOptions>(config.GetSection("Wx"));
        services.Configure<JwtOptions>(config.GetSection("Jwt"));
        // access_token 缓存必须单例：typed HttpClient 下面挂实例字段等于没缓存（每次调用都重新取 token）
        services.AddSingleton<Wx.WxTokenCache>();
        services.AddHttpClient<IWxApiClient, WxApiClient>();
        services.AddSingleton<IJwtIssuer, JwtIssuer>();
        // 导出下载链接签名：复用同一份密钥，但 token 不带 JWT 头（它只是预签名 URL，不是 API 凭证）
        services.AddSingleton<IExportSigner, HmacExportSigner>();

        // 后台服务（可用 Workers:Enabled=false 关掉，例如集成测试里）
        if (config.GetValue("Workers:Enabled", true))
        {
            services.AddHostedService<Workers.MaterializerWorker>();
            services.AddHostedService<Workers.ReminderTickWorker>();
            services.AddHostedService<Workers.PushWorker>();
            services.AddHostedService<Workers.WeekReportWorker>();
            services.AddHostedService<Workers.ArchiveWorker>();
            services.AddHostedService<Workers.StatsWorker>();
        }

        return services;
    }

    public static IServiceCollection AddMedBoxApplication(this IServiceCollection services)
    {
        services.AddScoped<IMemberGuard, MemberGuard>();
        services.AddScoped<ConfigReader>();
        services.AddScoped<DoseMaterializer>();
        services.AddScoped<DoseCasApplier>();
        services.AddScoped<EntitlementService>();
        services.AddScoped<PushTaskBuilder>();

        services.AddScoped<LoginHandler>();
        services.AddScoped<UserAgreeHandler>();
        services.AddScoped<FamilyInfoHandler>();
        services.AddScoped<FamilyListHandler>();
        services.AddScoped<FamilyCreateHandler>();
        services.AddScoped<FamilyJoinHandler>();
        services.AddScoped<FamilyUpdateRoleHandler>();
        services.AddScoped<FamilyLeaveHandler>();
        services.AddScoped<FamilyResetInviteHandler>();
        services.AddScoped<PatientListHandler>();
        services.AddScoped<PatientCreateHandler>();
        services.AddScoped<PatientUpdateHandler>();
        services.AddScoped<PatientArchiveHandler>();
        services.AddScoped<MedicineListHandler>();
        services.AddScoped<CommonMedsHandler>();
        services.AddScoped<MedicineCreateHandler>();
        services.AddScoped<MedicineUpdateHandler>();
        services.AddScoped<MedicineArchiveHandler>();
        services.AddScoped<PlanUpsertHandler>();
        services.AddScoped<PlanFinishHandler>();
        services.AddScoped<TodayHandler>();
        services.AddScoped<WeekHandler>();
        services.AddScoped<SubmitHandler>();
        services.AddScoped<SnoozeHandler>();
        services.AddScoped<CorrectHandler>();
        services.AddScoped<ConfigGetHandler>();
        services.AddScoped<QuotaHandler>();
        services.AddScoped<NotifyAckHandler>();
        services.AddScoped<ReportBatchHandler>();
        services.AddScoped<ExportHandler>();
        services.AddScoped<EraseHandler>();

        return services;
    }
}
