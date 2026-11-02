using MedBox.Application.Abstractions;
using MedBox.Infrastructure.Logging;
using MedBox.Infrastructure.Persistence;
using MedBox.Infrastructure.Wx;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MedBox.Integration.Tests.TestSupport;

/// <summary>把所有用例挂到同一个 xUnit collection 上，容器只起一次。</summary>
[CollectionDefinition(Name)]
public sealed class MedBoxCollection : ICollectionFixture<PostgresContainerFixture>
{
    public const string Name = "MedBox integration";
}

/// <summary>
/// WebApplicationFactory 的封装：把 API 指到测试容器，并掐掉所有会打到微信 / 开发库的分支。
///
/// ⚠ 连接串的覆写方式是有意的，别"简化"成只留 ConfigureAppConfiguration：
/// MedBox.Api 配了 UserSecretsId，而 user-secrets 里的 ConnectionStrings:MedBox 指向**开发库**。
/// 只靠 ConfigureAppConfiguration 的 in-memory 集合压不过它（实测 API 连到了开发库，
/// 症状是"种子明明插进去了，family/info 却回 1002"）。所以这里用两道保证：
///   ① 构造时写环境变量 —— env vars 在配置链里排在 appsettings / user-secrets 之后，优先级更高；
///   ② ConfigureTestServices 里 RemoveAll 后按测试容器重注册 MedBoxDbContext / IAppDbContext ——
///      ConfigureTestServices 保证跑在应用的 ConfigureServices 之后，这一道必胜。
///
/// 另外四件必须覆写的事：
///   - Workers:Enabled=false —— 后台 6 个 Worker 不该在测试里跑（会和用例抢同一批 dose_logs）。
///   - DbLogWriter 摘掉 —— 它按 MedBoxConnection 往 app_logs 写日志，测试里没有观察它的必要，
///     更不能让它往开发库写。
///   - IWxApiClient 换成替身 —— AGENTS.md §9：测试绝不打真实微信接口
///     （code2session / subscribeMessage.send / msgSecCheck / access_token 一个都不行）。
///   - JWT__SECRET 固定下来，测试才能铸一个能过鉴权的 token。
/// </summary>
public sealed class MedBoxApiFactory : WebApplicationFactory<Program>
{
    /// <summary>测试专用秘钥，只存在于进程内，不进 git。</summary>
    public const string JwtSecret = "integration-test-secret-0123456789abcdef-0123456789abcdef";

    private readonly string _connectionString;

    public MedBoxApiFactory(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

        // 必须在宿主构建之前写。宿主是懒构建的（第一次 CreateClient / Services 时才建），所以放构造函数里有效。
        Environment.SetEnvironmentVariable("ConnectionStrings__MedBox", _connectionString);
        Environment.SetEnvironmentVariable("Workers__Enabled", "false");
        Environment.SetEnvironmentVariable("Logging__Db__Enabled", "false");
        Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
        // 双保险：即使替身没换上，空 secret + 关掉 msgSecCheck 也一个请求都发不出去
        Environment.SetEnvironmentVariable("Wx__AppSecret", "");
        Environment.SetEnvironmentVariable("Wx__MsgSecCheckEnabled", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MedBox"] = _connectionString,
                ["Workers:Enabled"] = "false",
                ["Logging:Db:Enabled"] = "false",
                ["Logging:LogLevel:Default"] = "Warning",
                ["JWT__SECRET"] = JwtSecret,
                ["Wx:AppSecret"] = "",
                ["Wx:MsgSecCheckEnabled"] = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // ① 连接串：先摘掉 AddMedBoxInfrastructure 注册的那两个，再按测试容器重注册
            services.RemoveAll<MedBoxDbContext>();
            services.RemoveAll<IAppDbContext>();
            services.AddScoped<MedBoxDbContext>(_ => new MedBoxDbContext(
                new DbContextOptionsBuilder<MedBoxDbContext>().UseNpgsql(_connectionString).Options));
            services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<MedBoxDbContext>());

            // ② 日志落库：摘掉，别让它往任何库写 app_logs
            services.RemoveAll<DbLogWriter>();

            // ③ 微信接口替身（后注册的赢，把 typed HttpClient 的 WxApiClient 顶掉）
            services.AddScoped<IWxApiClient, FakeWxApiClient>();
        });
    }

    /// <summary>给指定 openid 铸一个能过 [Authorize] 的 access token。</summary>
    public string MintToken(string openid)
        => Services.GetRequiredService<MedBox.Application.Abstractions.IJwtIssuer>().Issue(openid);
}

/// <summary>
/// 微信接口替身。三个方法一个都不发请求：
/// code2session 返回固定 openid（测试不依赖它，token 由 IJwtIssuer 直接铸）、
/// 订阅消息一律"发不了"、内容安全一律放行（微信抖动时的生产口径也是放行）。
/// </summary>
internal sealed class FakeWxApiClient : IWxApiClient
{
    public Task<WxSession> Code2SessionAsync(string code, CancellationToken ct = default)
        => Task.FromResult(new WxSession("fake_openid", null, null));

    public Task<WxSendResult> SendSubscribeAsync(
        string openid, string tmplId, string page,
        IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
        => Task.FromResult(new WxSendResult(false, -1, "集成测试替身：不发送订阅消息"));

    public Task<WxSecVerdict> MsgSecCheckAsync(string content, string openid, CancellationToken ct = default)
        => Task.FromResult(WxSecVerdict.Pass);
}
