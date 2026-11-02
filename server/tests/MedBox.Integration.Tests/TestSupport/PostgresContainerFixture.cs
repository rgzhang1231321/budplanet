using Npgsql;
using Testcontainers.PostgreSql;

namespace MedBox.Integration.Tests.TestSupport;

/// <summary>
/// 整个测试程序集共享一个 postgres:18-alpine 容器（xUnit 的 ICollectionFixture）。
///
/// 为什么必须共享：每个测试类起一个容器的话，一次 dotnet test 要拉十几次镜像、建十几次 schema，
/// 单是启动就要几分钟。这里一次性起容器 → 一次性跑完全部 V*.sql → 所有用例复用，
/// 单个用例之间靠"各自用独立的 family_id / pid"互不干扰。
///
/// 铁律（AGENTS.md §5.6 / §9）：
///   ⛔ 绝不连 medbox（本地开发库）或 ragdb（主理人另一个项目的库）。
///   连接串只能来自 Testcontainers 动态映射端口，没有任何代码路径能指向 5432 上的开发库。
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    /// <summary>测试专用库名。故意不叫 medbox —— 名字不一样，误连时一眼看得出来。</summary>
    private const string TestDatabase = "medbox_it";

    private PostgreSqlContainer? _container;

    public string ConnectionString => _container?.GetConnectionString()
        ?? throw new InvalidOperationException("容器还没启动，先在 InitializeAsync 里等它起来。");

    /// <summary>本次实际执行了的迁移脚本条数。</summary>
    public int AppliedMigrationCount { get; private set; }

    /// <summary>整个程序集共享一个 API 宿主（不是每个测试建一个）。</summary>
    public MedBoxApiFactory ApiFactory { get; private set; } = null!;

    /// <summary>带 Bearer token 的 HTTP 客户端封装。</summary>
    public ApiClient Api { get; private set; } = null!;

    /// <summary>直连测试库灌种子数据。</summary>
    public Seeder Seed { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase(TestDatabase)
            .WithUsername("it")
            .WithPassword("it")
            .Build();

        await _container.StartAsync();

        AppliedMigrationCount = await MigrationRunner.ApplyAsync(ConnectionString);

        ApiFactory = new MedBoxApiFactory(ConnectionString);
        Api = new ApiClient(ApiFactory.CreateClient(), openid => ApiFactory.MintToken(openid));
        Seed = new Seeder(this);
    }

    public async Task DisposeAsync()
    {
        ApiFactory?.Dispose();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    // ── 给用例用的几个薄封装 ──────────────────────────────────

    public async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        var result = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result, typeof(T))!;
    }

    public async Task<int> ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public NpgsqlParameter P(string name, object value) => new(name, value);
}
