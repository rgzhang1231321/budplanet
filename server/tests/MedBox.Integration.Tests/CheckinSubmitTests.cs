using System.Net;
using System.Text.Json;
using FluentAssertions;
using MedBox.Integration.Tests.TestSupport;
using Npgsql;

namespace MedBox.Integration.Tests;

/// <summary>
/// checkin.submit 的"部分成功不回滚 + 批量上限"（AGENTS.md §7.5 契约冻结）。
///
/// 为什么部分成功是**合法**而不是缺陷：老人一次打卡经常是"早上的记了、晚上的还没到点"，
/// 或者某一条已经被漏服判定抢先改掉。把整批回滚意味着一条坏数据让好的那条也丢，
/// 而老人的操作是不可逆的 —— 他点完就放下了手机。
///
/// 所以契约写死：applied[].changed 区分真写入/幂等空操作（两种都出队），
/// rejected[] 仅 1005/1003 留队，绝不分批回滚。
/// </summary>
[Collection(MedBoxCollection.Name)]
public sealed class CheckinSubmitTests
{
    private readonly PostgresContainerFixture _fx;

    public CheckinSubmitTests(PostgresContainerFixture fx) => _fx = fx;

    private const int SlotMorning = 480;   // 08:00
    private const int SlotEvening = 1200;  // 20:00

    /// <summary>家庭 + admin + 服药人 + 计划 + 今天两行 pending 剂量。</summary>
    private async Task<(string Fid, string Openid, string Pid, string Date)> SeedTodayAsync(string tag)
    {
        var fid = "F_ck_" + tag;
        var openid = "openid_ck_" + tag;
        var pid = "P_ck_" + tag;
        var date = Seeder.TodayCn();

        await _fx.Seed.UserAsync(openid);
        await _fx.Seed.FamilyAsync(fid, openid);
        await _fx.Seed.MemberAsync(fid, openid, "admin");
        await _fx.Seed.PatientAsync(pid, fid);
        await _fx.Seed.MedicineAsync("M_ck_" + tag, fid, pid);
        await _fx.Seed.PlanAsync("PL_ck_" + tag, fid, pid, "M_ck_" + tag, date, [SlotMorning, SlotEvening]);
        await _fx.Seed.DoseLogAsync($"{pid}_{date}_{SlotMorning}", fid, pid, "PL_ck_" + tag, "M_ck_" + tag, date, SlotMorning);
        await _fx.Seed.DoseLogAsync($"{pid}_{date}_{SlotEvening}", fid, pid, "PL_ck_" + tag, "M_ck_" + tag, date, SlotEvening);

        return (fid, openid, pid, date);
    }

    private async Task<string> StatusOfAsync(string doseId)
        => await _fx.ScalarAsync<string>(
            "SELECT status FROM dose_logs WHERE dose_id = @id", _fx.P("id", doseId));

    // ── 部分成功不回滚 ───────────────────────────────────────

    /// <summary>
    /// 一条好的一条坏的：好的仍然写入、坏的进 rejected，**不整批回滚**。
    /// 坏的那条用"缺 slot"（反序列化成 0）→ 拼出的 dose_id 在库里不存在 → 1005。
    /// </summary>
    [Fact]
    public async Task 混一条坏记录时好的仍写入坏的进rejected()
    {
        var (fid, openid, pid, date) = await SeedTodayAsync("partial");

        var (status, body) = await _fx.Api.PostAsync("/api/checkin/submit", new
        {
            items = new object[]
            {
                new { pid, date, slot = SlotMorning, status = "taken" },
                new { pid, date, status = "taken" },   // 缺 slot
            }
        }, openid);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("code").GetInt32().Should().Be(0);

        var data = body.GetProperty("data");
        var applied = data.GetProperty("applied").EnumerateArray().ToList();
        var rejected = data.GetProperty("rejected").EnumerateArray().ToList();

        applied.Should().HaveCount(1);
        applied[0].GetProperty("pid").GetString().Should().Be(pid);
        applied[0].GetProperty("slot").GetInt32().Should().Be(SlotMorning);
        applied[0].GetProperty("status").GetString().Should().Be("taken");
        applied[0].GetProperty("changed").GetBoolean().Should().BeTrue("这一条真的写进去了");

        rejected.Should().HaveCount(1);
        rejected[0].GetProperty("code").GetInt32().Should().Be(1005);

        // 关键：好的那条**确实落库了**，没有被整批回滚
        (await StatusOfAsync($"{pid}_{date}_{SlotMorning}")).Should().Be("taken");
        // 坏的那条什么都没改
        (await StatusOfAsync($"{pid}_{date}_{SlotEvening}")).Should().Be("pending");
    }

    /// <summary>状态不合法也走 rejected，同样不回滚。</summary>
    [Fact]
    public async Task 状态不合法时其他条仍然写入()
    {
        var (fid, openid, pid, date) = await SeedTodayAsync("badstatus");

        var (_, body) = await _fx.Api.PostAsync("/api/checkin/submit", new
        {
            items = new object[]
            {
                new { pid, date, slot = SlotMorning, status = "half_eaten" },  // 不在四态里
                new { pid, date, slot = SlotEvening, status = "taken" },
            }
        }, openid);

        var data = body.GetProperty("data");
        data.GetProperty("applied").EnumerateArray().Should().HaveCount(1);
        data.GetProperty("rejected").EnumerateArray().Should().HaveCount(1);
        data.GetProperty("rejected")[0].GetProperty("code").GetInt32().Should().Be(1005);

        (await StatusOfAsync($"{pid}_{date}_{SlotEvening}")).Should().Be("taken");
        (await StatusOfAsync($"{pid}_{date}_{SlotMorning}")).Should().Be("pending");
    }

    // ── 幂等：重复提交同一行 changed=false 且不报错 ────────────

    [Fact]
    public async Task 重复提交同一行是幂等空操作()
    {
        var (fid, openid, pid, date) = await SeedTodayAsync("idem");
        var payload = new
        {
            items = new object[] { new { pid, date, slot = SlotMorning, status = "taken" } }
        };

        var (_, first) = await _fx.Api.PostAsync("/api/checkin/submit", payload, openid);
        var (_, second) = await _fx.Api.PostAsync("/api/checkin/submit", payload, openid);

        first.GetProperty("data").GetProperty("applied")[0].GetProperty("changed").GetBoolean()
            .Should().BeTrue();
        second.GetProperty("code").GetInt32().Should().Be(0);
        second.GetProperty("data").GetProperty("applied")[0].GetProperty("changed").GetBoolean()
            .Should().BeFalse("第二次是幂等空操作，两种都出队（契约 §7.5）");
    }

    // ── 批量上限 ─────────────────────────────────────────────

    [Theory]
    [InlineData(9)]
    [InlineData(20)]
    public async Task 单批超过8条直接1005且一条都不写(int count)
    {
        var (fid, openid, pid, date) = await SeedTodayAsync("over" + count);

        var items = Enumerable.Range(0, count)
            .Select(i => new { pid, date, slot = SlotMorning + i, status = "taken" })
            .ToArray();

        var (status, body) = await _fx.Api.PostAsync("/api/checkin/submit", new { items }, openid);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("code").GetInt32().Should().Be(1005);

        // 整批被挡在门外，什么都没写
        (await StatusOfAsync($"{pid}_{date}_{SlotMorning}")).Should().Be("pending");
    }

    [Fact]
    public async Task 正好8条可以提交()
    {
        var (fid, openid, pid, date) = await SeedTodayAsync("exactly8");

        var items = Enumerable.Range(0, 8)
            .Select(i => new { pid, date, slot = 60 + i, status = "taken" })
            .ToArray();

        var (_, body) = await _fx.Api.PostAsync("/api/checkin/submit", new { items }, openid);

        body.GetProperty("code").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task 空items是1005()
    {
        var (fid, openid, _, _) = await SeedTodayAsync("empty");

        var (_, body) = await _fx.Api.PostAsync("/api/checkin/submit", new { items = Array.Empty<object>() }, openid);

        body.GetProperty("code").GetInt32().Should().Be(1005);
    }

    /// <summary>一次只能提交同一个家庭的记录（入参没有 fid，家庭从 pid 反查且必须唯一）。</summary>
    [Fact]
    public async Task 跨家庭混批是1005()
    {
        var (fid1, openid1, pid1, date) = await SeedTodayAsync("mix1");
        var fid2 = "F_ck_mix2";
        var pid2 = "P_ck_mix2";
        await _fx.Seed.UserAsync("openid_ck_mix2");
        await _fx.Seed.FamilyAsync(fid2, "openid_ck_mix2");
        await _fx.Seed.MemberAsync(fid2, "openid_ck_mix2", "admin");
        await _fx.Seed.PatientAsync(pid2, fid2);
        await _fx.Seed.MedicineAsync("M_ck_mix2", fid2, pid2);
        await _fx.Seed.PlanAsync("PL_ck_mix2", fid2, pid2, "M_ck_mix2", date, [SlotMorning]);
        await _fx.Seed.DoseLogAsync($"{pid2}_{date}_{SlotMorning}", fid2, pid2, "PL_ck_mix2", "M_ck_mix2", date, SlotMorning);

        var (_, body) = await _fx.Api.PostAsync("/api/checkin/submit", new
        {
            items = new object[]
            {
                new { pid = pid1, date, slot = SlotMorning, status = "taken" },
                new { pid = pid2, date, slot = SlotMorning, status = "taken" },
            }
        }, openid1);

        body.GetProperty("code").GetInt32().Should().Be(1005);
        (await StatusOfAsync($"{pid1}_{date}_{SlotMorning}")).Should().Be("pending");
    }

    /// <summary>出参带 quota 与 server_ts（契约 §7.5：applied 两种都出队，调用方要能刷额度）。</summary>
    [Fact]
    public async Task 出参带quota和server_ts()
    {
        var (fid, openid, pid, date) = await SeedTodayAsync("quota");

        var (_, body) = await _fx.Api.PostAsync("/api/checkin/submit", new
        {
            items = new object[] { new { pid, date, slot = SlotMorning, status = "taken" } }
        }, openid);

        var data = body.GetProperty("data");
        data.GetProperty("server_ts").GetInt64().Should().BeGreaterThan(1_600_000_000_000L);
        data.GetProperty("quota").ValueKind.Should().Be(JsonValueKind.Object);
    }
}
