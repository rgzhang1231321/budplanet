using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;

namespace MedBox.Integration.Tests.TestSupport;

/// <summary>
/// db/apply.sh 的进程内等价物：把 <c>db/migrations/V*.sql</c> 按文件名顺序逐条执行，
/// 并往 schema_migrations 账本里记一行（version / checksum / execution_ms / script_name）。
///
/// 为什么不用 EF 的 EnsureCreated / Migrate：AGENTS.md §5.1 禁用 EF Migration，
/// schema 的唯一真相源是 db/migrations/ 下的 SQL 脚本。db/apply.sh 本质上也是
/// "把同一批文件按顺序喂给 psql，再记一行账本"，脚本内容是纯 SQL、没有任何 psql 元命令，
/// 所以测试进程可以直接执行**字节相同**的内容 —— 集成测试验的正是"脚本能重建 schema"。
///
/// 账本校验和与 apply.sh 的 sha256sum 口径一致（小写十六进制），
/// 所以同一份脚本在测试库和开发库里记下的 checksum 是同一个值，可以互相校对。
/// </summary>
internal static class MigrationRunner
{
    /// <summary>环境变量可显式指定迁移目录（排查用，正常不需要）。</summary>
    private const string MigrationsDirEnvVar = "MEDBOX_MIGRATIONS_DIR";

    public static string MigrationsDirectory { get; } = FindMigrationsDirectory();

    /// <summary>仓库里的迁移脚本（按文件名 = 版本号排序）。</summary>
    public static IReadOnlyList<string> ScriptFiles { get; } =
        Directory.GetFiles(MigrationsDirectory, "V*.sql")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// 建 schema + 记账本。已记账且校验和一致的版本跳过（与 apply.sh 同语义，可安全重跑）。
    /// </summary>
    public static async Task<int> ApplyAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await BootstrapLedgerAsync(conn, ct);

        foreach (var file in ScriptFiles)
        {
            var scriptName = Path.GetFileName(file);
            var version = scriptName.Split("__", 2)[0];
            var bytes = await File.ReadAllBytesAsync(file, ct);
            var checksum = Convert.ToHexStringLower(SHA256.HashData(bytes));

            if (await AlreadyAppliedAsync(conn, version, checksum, ct)) continue;

            var watch = Stopwatch.StartNew();
            await using (var cmd = new NpgsqlCommand(System.Text.Encoding.UTF8.GetString(bytes), conn))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            watch.Stop();

            await using (var ledger = new NpgsqlCommand(
                """
                INSERT INTO schema_migrations (version, checksum, execution_ms, script_name)
                VALUES (@version, @checksum, @ms, @name)
                """, conn))
            {
                ledger.Parameters.AddWithValue("version", version);
                ledger.Parameters.AddWithValue("checksum", checksum);
                ledger.Parameters.AddWithValue("ms", (int)watch.ElapsedMilliseconds);
                ledger.Parameters.AddWithValue("name", scriptName);
                await ledger.ExecuteNonQueryAsync(ct);
            }
        }

        return ScriptFiles.Count;
    }

    /// <summary>
    /// 账本表的 bootstrap —— 与 db/apply.sh 里那段逐字一致。
    /// 必须在跑 V001 之前建：V001 里那条同名 CREATE 带 IF NOT EXISTS，建过了就不会再生效。
    /// 两处 execution_ms 类型不同（这里是 BIGINT，V001 里是 INTEGER）是历史原因，
    /// apply.sh 的注释明确写了"不要为了对齐去改 V001"。
    /// </summary>
    private static async Task BootstrapLedgerAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
              version VARCHAR(16) PRIMARY KEY,
              checksum VARCHAR(64) NOT NULL,
              applied_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              execution_ms BIGINT NOT NULL DEFAULT 0,
              script_name VARCHAR(255) NOT NULL
            )
            """;
        await using var cmd = new NpgsqlCommand(ddl, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> AlreadyAppliedAsync(
        NpgsqlConnection conn, string version, string checksum, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT checksum FROM schema_migrations WHERE version = @version", conn);
        cmd.Parameters.AddWithValue("version", version);

        var existing = await cmd.ExecuteScalarAsync(ct) as string;
        if (existing is null) return false;

        if (!string.Equals(existing, checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{version} 已执行但校验和不符 —— 迁移脚本被改过。append-only 铁律：要改表就新增版本，不许改旧脚本。");
        }

        return true;
    }

    /// <summary>
    /// 按 环境变量 → 输出目录里的拷贝 → 测试程序集目录逐级向上 → 当前工作目录逐级向上 的顺序找 db/migrations。
    /// 第二条是主路径：csproj 把仓库里的 V*.sql 原样拷进了输出目录（见 csproj 注释），
    /// 所以产物落在哪都能找到；后两条是兜底（手动 --no-build 到旧产物目录之类的情况）。
    /// </summary>
    private static string FindMigrationsDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable(MigrationsDirEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;

        var copied = Path.Combine(AppContext.BaseDirectory, "db", "migrations");
        if (Directory.Exists(copied)) return copied;

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "db", "migrations");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }

        throw new InvalidOperationException(
            $"找不到 db/migrations 目录（试过 {AppContext.BaseDirectory} 与 {Directory.GetCurrentDirectory()}）。" +
            "集成测试必须用仓库里的迁移脚本建 schema，不能用 EF EnsureCreated；" +
            $"也可以用环境变量 {MigrationsDirEnvVar} 显式指定。");
    }
}
