using FluentAssertions;
using Npgsql;
using MedBox.Integration.Tests.TestSupport;

namespace MedBox.Integration.Tests;

/// <summary>
/// schema 必须由 db/migrations/V*.sql 重建，绝不用 EF EnsureCreated（AGENTS.md §5.1 / §9）。
/// 这一组断言就是那条铁律的验收：脚本能从头建出一套可用的 schema，
/// 且几个"代码依赖它才正确"的约束（主键、CHECK、索引）真的在。
/// </summary>
[Collection(MedBoxCollection.Name)]
public sealed class SchemaMigrationTests
{
    private readonly PostgresContainerFixture _fx;

    public SchemaMigrationTests(PostgresContainerFixture fx) => _fx = fx;

    [Fact]
    public async Task 迁移账本行数等于迁移脚本文件数()
    {
        var expected = MigrationRunner.ScriptFiles.Count;
        expected.Should().BeGreaterThan(0, "仓库里应该有迁移脚本");

        var actual = await _fx.ScalarAsync<long>("SELECT count(*) FROM schema_migrations");

        actual.Should().Be(expected,
            $"db/migrations 下有 {expected} 个 V*.sql，schema_migrations 就该有 {expected} 行");
    }

    [Fact]
    public async Task dose_logs主键约束名叫dose_logs_pkey()
    {
        var pkey = await _fx.ScalarAsync<string>(
            """
            SELECT conname FROM pg_constraint
            WHERE conrelid = 'dose_logs'::regclass AND contype = 'p'
            """);

        pkey.Should().Be("dose_logs_pkey");
    }

    /// <summary>
    /// 四态冻结（pending|taken|missed|skipped）靠 CHECK 约束保证，不是应用层自觉。
    /// 插一个域外的值必须被库直接拒掉 —— 这也是"加值也要写脚本"的物理依据。
    /// </summary>
    [Fact]
    public async Task dose_logs的status有CHECK约束且域外值被拒()
    {
        var definition = await _fx.ScalarAsync<string>(
            """
            SELECT pg_get_constraintdef(oid) FROM pg_constraint
            WHERE conrelid = 'dose_logs'::regclass AND contype = 'c'
              AND pg_get_constraintdef(oid) ILIKE '%status%'
            """);

        definition.Should().Contain("pending").And.Contain("taken")
            .And.Contain("missed").And.Contain("skipped");

        // 真的拦得住：往 CHECK 里塞一个第五态，库要拒
        var act = async () => await _fx.ExecuteAsync(
            """
            INSERT INTO dose_logs (dose_id, family_id, patient_id, plan_id, medicine_id,
                                   due_date, slot, due_ts, status, created_at)
            VALUES ('__check_probe__', 'f', 'p', 'pl', 'm', '2026-11-02', 480, 0, 'late', 0)
            """);

        await act.Should().ThrowAsync<PostgresException>();
    }

    /// <summary>V012 新增的 week_key 索引（审计 §13.4.1：周报谓词改 week_key 后靠它命中）。</summary>
    [Fact]
    public async Task week_key索引存在且是部分索引()
    {
        var definition = await _fx.ScalarAsync<string>(
            """
            SELECT indexdef FROM pg_indexes
            WHERE tablename = 'dose_logs' AND indexname = 'ix_dose_logs_week_key'
            """);

        definition.Should().Contain("week_key");
        // 部分索引：本批之前生成的历史行该列为 NULL，全索引会把永不被命中的 NULL 行也建进去
        definition.Should().Contain("WHERE");
    }

    /// <summary>dose_id 的确定性口径：${pid}_${date}_${slot}，主键本身就是幂等键。</summary>
    [Fact]
    public async Task 重复dose_id插入被主键拒绝()
    {
        await _fx.ExecuteAsync(
            """
            INSERT INTO dose_logs (dose_id, family_id, patient_id, plan_id, medicine_id,
                                   due_date, slot, due_ts, status, created_at)
            VALUES ('__pk_probe__', 'f', 'p', 'pl', 'm', '2026-11-02', 480, 0, 'pending', 0)
            """);

        var act = async () => await _fx.ExecuteAsync(
            """
            INSERT INTO dose_logs (dose_id, family_id, patient_id, plan_id, medicine_id,
                                   due_date, slot, due_ts, status, created_at)
            VALUES ('__pk_probe__', 'f', 'p', 'pl', 'm', '2026-11-02', 480, 0, 'pending', 0)
            """);

        await act.Should().ThrowAsync<PostgresException>();
    }

    /// <summary>family_members 的 role 也是 CHECK（三值冻结）。</summary>
    [Fact]
    public async Task family_members的role有CHECK约束()
    {
        var definition = await _fx.ScalarAsync<string>(
            """
            SELECT pg_get_constraintdef(oid) FROM pg_constraint
            WHERE conrelid = 'family_members'::regclass AND contype = 'c'
              AND pg_get_constraintdef(oid) ILIKE '%role%'
            """);

        definition.Should().Contain("admin").And.Contain("member").And.Contain("elder");
    }

    /// <summary>
    /// 隔离铁律（AGENTS.md §5.6 / §9）：连接串只能来自 Testcontainers 动态端口。
    /// 这条断言不是形式主义 —— 它保证"忘了覆写 ConnectionStrings:MedBox"这类错误
    /// 会让测试炸在这里，而不是静默把测试数据写进开发库 medbox。
    /// </summary>
    [Fact]
    public void 连接串来自测试容器且不指向开发库或ragdb()
    {
        _fx.ConnectionString.Should().NotBeNullOrWhiteSpace();
        _fx.ConnectionString.Should().NotContain("ragdb");
        // 开发库固定 127.0.0.1:5432；Testcontainers 是随机高位端口
        _fx.ConnectionString.Should().NotContain("Port=5432");
        _fx.ConnectionString.Should().NotContain("Host=127.0.0.1;Port=5432");
    }

    /// <summary>V003 的 app_config 种子必须就位（限额、阈值都从它读）。</summary>
    [Fact]
    public async Task app_config种子就位且freeMedLimit可读()
    {
        var json = await new Seeder(_fx).ConfigValueAsync();

        json.Should().Contain("freeMedLimit");
        json.Should().Contain("snoozeDeadlineMin");
        json.Should().Contain("interstitialCool");
    }

    /// <summary>V003 的常用药种子必须就位（/api/medicine/common 是公开只读端点）。</summary>
    [Fact]
    public async Task 常用药种子就位()
    {
        var count = await _fx.ScalarAsync<long>("SELECT count(*) FROM medicines_common");

        count.Should().BeGreaterThanOrEqualTo(6);
    }
}
