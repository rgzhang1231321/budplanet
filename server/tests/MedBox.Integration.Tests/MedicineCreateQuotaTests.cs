using System.Net;
using System.Text.Json;
using FluentAssertions;
using MedBox.Integration.Tests.TestSupport;

namespace MedBox.Integration.Tests;

/// <summary>
/// medicine.create 的药品名额限额（审计 §13.4 第 8 项，AGENTS.md §6.2）。
///
/// 核心口径：限额值必须从 <c>app_config.freeMedLimit</c> 读，**不许硬编码**。
/// 硬编码的后果不是"以后改不了"，而是后端与 config 域下发两套数：
/// 前端按 config 显示"还能加 1 个"，后端按自己的常量放行到 3 个，两边永远对不上。
/// 所以这里除了验默认值，还要真的改一次配置复测。
/// </summary>
[Collection(MedBoxCollection.Name)]
public sealed class MedicineCreateQuotaTests
{
    private readonly PostgresContainerFixture _fx;

    public MedicineCreateQuotaTests(PostgresContainerFixture fx) => _fx = fx;

    private async Task<(string Fid, string Openid, string Uid)> SeedFamilyAsync(string tag, int existingMeds)
    {
        var fid = "F_md_" + tag;
        var openid = "openid_md_" + tag;
        var uid = "P_md_" + tag;

        await _fx.Seed.UserAsync(openid);
        await _fx.Seed.FamilyAsync(fid, openid);
        await _fx.Seed.MemberAsync(fid, openid, "admin");
        await _fx.Seed.PatientAsync(uid, fid);
        for (var i = 0; i < existingMeds; i++)
            await _fx.Seed.MedicineAsync($"M_md_{tag}_{i}", fid, uid);

        return (fid, openid, uid);
    }

    private async Task<int> ActiveMedCountAsync(string fid)
        => (int)await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM medicines WHERE family_id = @fid AND status = 'active'",
            _fx.P("fid", fid));

    // ── 默认限额（V003 种子 freeMedLimit = 3）─────────────────

    [Fact]
    public async Task 未超限额时可以创建()
    {
        var (fid, openid, uid) = await SeedFamilyAsync("under", existingMeds: 2);

        var (status, body) = await _fx.Api.PostAsync("/api/medicine/create",
            new { fid, uid, name = "第3个药", spec = "5mg×28片", default_dose = "1片" }, openid);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("code").GetInt32().Should().Be(0);
        body.GetProperty("data").GetProperty("mid").GetString().Should().StartWith("M");
        (await ActiveMedCountAsync(fid)).Should().Be(3);
    }

    [Fact]
    public async Task 超过免费名额时返回1004且不写入()
    {
        var (fid, openid, uid) = await SeedFamilyAsync("over", existingMeds: 3);

        var (status, body) = await _fx.Api.PostAsync("/api/medicine/create",
            new { fid, uid, name = "第4个药", spec = "5mg×28片" }, openid);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("code").GetInt32().Should().Be(1004);
        body.GetProperty("msg").GetString().Should().NotBeNullOrEmpty();
        (await ActiveMedCountAsync(fid)).Should().Be(3, "1004 时一个都不能多写");
    }

    /// <summary>默认值确实是 3，不是别的数 —— 防止 V003 种子被改动后这里悄悄漂移。</summary>
    [Fact]
    public async Task 默认免费名额是3()
    {
        var json = await _fx.Seed.ConfigValueAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("freeMedLimit").GetInt32().Should().Be(3);
    }

    // ── 限额值来自 app_config，不是硬编码 ─────────────────────

    /// <summary>
    /// 把 app_config.freeMedLimit 改成 1，第 2 个药就该被拒。
    /// 如果实现里写死了 3，这条会失败 —— 这正是它存在的意义。
    /// </summary>
    [Fact]
    public async Task 把配置改成1之后第2个药就被拒()
    {
        var (fid, openid, uid) = await SeedFamilyAsync("cfg1", existingMeds: 1);

        var original = await _fx.Seed.ConfigValueAsync();
        // ConfigReader 是进程内 60s 缓存的 Singleton：共享宿主可能还记着旧值，
        // 所以这里单独起一个宿主，让它第一次读就读到改过的配置。
        using var factory = new MedBoxApiFactory(_fx.ConnectionString);
        var api = new ApiClient(factory.CreateClient(), o => factory.MintToken(o));

        try
        {
            await _fx.Seed.SetConfigValueAsync(
                original.Replace("\"freeMedLimit\": 3", "\"freeMedLimit\": 1"));

            var (status, body) = await api.PostAsync("/api/medicine/create",
                new { fid, uid, name = "第2个药", spec = "5mg×28片" }, openid);

            status.Should().Be(HttpStatusCode.OK);
            body.GetProperty("code").GetInt32().Should().Be(1004);
            (await ActiveMedCountAsync(fid)).Should().Be(1);
        }
        finally
        {
            // 还原配置，别让后面的用例受影响
            await _fx.Seed.SetConfigValueAsync(original);
        }
    }

    /// <summary>对称的一条：把配置放大到 5，第 4 个药就放行。</summary>
    [Fact]
    public async Task 把配置改成5之后第4个药放行()
    {
        var (fid, openid, uid) = await SeedFamilyAsync("cfg5", existingMeds: 3);

        var original = await _fx.Seed.ConfigValueAsync();
        using var factory = new MedBoxApiFactory(_fx.ConnectionString);
        var api = new ApiClient(factory.CreateClient(), o => factory.MintToken(o));

        try
        {
            await _fx.Seed.SetConfigValueAsync(
                original.Replace("\"freeMedLimit\": 3", "\"freeMedLimit\": 5"));

            var (status, body) = await api.PostAsync("/api/medicine/create",
                new { fid, uid, name = "第4个药", spec = "5mg×28片" }, openid);

            status.Should().Be(HttpStatusCode.OK);
            body.GetProperty("code").GetInt32().Should().Be(0);
            (await ActiveMedCountAsync(fid)).Should().Be(4);
        }
        finally
        {
            await _fx.Seed.SetConfigValueAsync(original);
        }
    }

    // ── 其他必拒分支（都是 1005，不是 1004）───────────────────

    [Fact]
    public async Task 药名为空是1005不是1004()
    {
        var (fid, openid, uid) = await SeedFamilyAsync("noname", existingMeds: 3);

        var (_, body) = await _fx.Api.PostAsync("/api/medicine/create",
            new { fid, uid, name = "   ", spec = "5mg×28片" }, openid);

        body.GetProperty("code").GetInt32().Should().Be(1005);
    }

    [Fact]
    public async Task 服药人不存在是1005()
    {
        var (fid, openid, _) = await SeedFamilyAsync("nopatient", existingMeds: 0);

        var (_, body) = await _fx.Api.PostAsync("/api/medicine/create",
            new { fid, uid = "P_does_not_exist", name = "阿司匹林肠溶片" }, openid);

        body.GetProperty("code").GetInt32().Should().Be(1005);
    }

    /// <summary>elder 不能建药（只能看自己的）→ 1003，且不占名额。</summary>
    [Fact]
    public async Task elder建药是1003()
    {
        var fid = "F_md_elder";
        var openid = "openid_md_elder";
        var uid = "P_md_elder";
        await _fx.Seed.UserAsync(openid);
        await _fx.Seed.FamilyAsync(fid, openid);
        await _fx.Seed.MemberAsync(fid, openid, "elder");
        await _fx.Seed.PatientAsync(uid, fid);

        var (_, body) = await _fx.Api.PostAsync("/api/medicine/create",
            new { fid, uid, name = "阿司匹林肠溶片", spec = "100mg×30片" }, openid);

        body.GetProperty("code").GetInt32().Should().Be(1003);
        (await ActiveMedCountAsync(fid)).Should().Be(0);
    }
}
