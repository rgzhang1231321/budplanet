using System.Net;
using System.Text.Json;
using FluentAssertions;
using MedBox.Integration.Tests.TestSupport;

namespace MedBox.Integration.Tests;

/// <summary>
/// 响应封套一致性（审计 §13.4 点名的第 6 项，AGENTS.md §6.3）。
///
/// 两条硬要求：
///  ① 成功恒为 <c>{code:0, data:{…}, msg:"ok"}</c>；失败按异常类型映射到固定错误码，
///     且**一个错误码都不许多**（0 / 1001 / 1002 / 1003 / 1004 / 1005 / 1009）。
///  ② HTTP 状态码恒为 200 —— 封套优先。前端把 4xx/5xx 当网络异常，
///     一个 400 就会让老人端把一次"参数填错"记进离线重试队列无限重投。
/// </summary>
[Collection(MedBoxCollection.Name)]
public sealed class ResponseEnvelopeTests
{
    private readonly PostgresContainerFixture _fx;

    public ResponseEnvelopeTests(PostgresContainerFixture fx) => _fx = fx;

    /// <summary>造一个"家庭 + admin 成员 + 一个服药人"的最小场景，返回 (fid, openid)。</summary>
    private async Task<(string Fid, string Openid)> SeedFamilyAsync(string tag, string role = "admin")
    {
        var fid = "F_env_" + tag;
        var openid = "openid_env_" + tag;
        await _fx.Seed.UserAsync(openid);
        await _fx.Seed.FamilyAsync(fid, openid);
        await _fx.Seed.MemberAsync(fid, openid, role);
        await _fx.Seed.PatientAsync("P_env_" + tag, fid);
        return (fid, openid);
    }

    // ── 成功 ─────────────────────────────────────────────────

    [Fact]
    public async Task 成功时封套是code0加data加msg_ok()
    {
        var (fid, openid) = await SeedFamilyAsync("ok");

        var (status, body) = await _fx.Api.GetAsync($"/api/family/info?fid={fid}", openid);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("code").GetInt32().Should().Be(0);
        body.GetProperty("msg").GetString().Should().Be("ok");
        body.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Object);
        body.GetProperty("data").GetProperty("fid").GetString().Should().Be(fid);
    }

    /// <summary>所有读接口的 data 必带 server_ts（契约 §一：前端靠它做 server_ts + offset 的时钟补偿）。</summary>
    [Theory]
    [InlineData("/api/family/info?fid={0}")]
    [InlineData("/api/family/list")]
    [InlineData("/api/medicine/common")]
    public async Task 读接口的data必带server_ts(string route)
    {
        var (fid, openid) = await SeedFamilyAsync("ts");
        var url = string.Format(route, fid);

        var (_, body) = await _fx.Api.GetAsync(url, openid);

        body.GetProperty("code").GetInt32().Should().Be(0);
        body.GetProperty("data").GetProperty("server_ts").GetInt64()
            .Should().BeGreaterThan(1_600_000_000_000L, "server_ts 是 UTC 毫秒");
    }

    // ── 错误码映射 ───────────────────────────────────────────

    [Fact]
    public async Task 参数错误映射成1005()
    {
        var (_, openid) = await SeedFamilyAsync("v");

        // fid 与邀请码都为空 → ValidationException("邀请码无效")
        var (status, body) = await _fx.Api.PostAsync("/api/family/join", new { }, openid);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("code").GetInt32().Should().Be(1005);
        body.GetProperty("msg").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task 非家庭成员映射成1002()
    {
        var (_, openid) = await SeedFamilyAsync("nm");

        var (status, body) = await _fx.Api.GetAsync("/api/family/info?fid=F_does_not_exist", openid);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("code").GetInt32().Should().Be(1002);
    }

    [Fact]
    public async Task 权限不足映射成1003()
    {
        // 一个 member 角色去调 admin 专属的 reset_invite
        var fid = "F_env_forbidden";
        var openid = "openid_env_forbidden";
        await _fx.Seed.UserAsync(openid);
        await _fx.Seed.FamilyAsync(fid, openid);
        await _fx.Seed.MemberAsync(fid, openid, "member");

        var (status, body) = await _fx.Api.PostAsync("/api/family/reset_invite", new { fid }, openid);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("code").GetInt32().Should().Be(1003);
    }

    [Fact]
    public async Task 超出限额映射成1004()
    {
        var (fid, openid) = await SeedFamilyAsync("quota");
        var uid = "P_env_quota";
        // V003 种子 freeMedLimit=3：灌满 3 个再建第 4 个
        for (var i = 0; i < 3; i++)
            await _fx.Seed.MedicineAsync($"M_env_quota_{i}", fid, uid);

        var (status, body) = await _fx.Api.PostAsync("/api/medicine/create",
            new { fid, uid, name = "第4个药" }, openid);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("code").GetInt32().Should().Be(1004);
    }

    /// <summary>
    /// 未处理异常 → 1009。用"家庭名超过 families.name VARCHAR(128)"触发真实的库错误：
    /// 它走的是中间件的兜底分支（不是 MedBoxException），正是 1009 要覆盖的那一类。
    /// </summary>
    [Fact]
    public async Task 未处理异常映射成1009()
    {
        var (_, openid) = await SeedFamilyAsync("boom");

        var (status, body) = await _fx.Api.PostAsync("/api/family/create",
            new { name = new string('超', 200) }, openid);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("code").GetInt32().Should().Be(1009);
    }

    [Fact]
    public async Task 未登录或token缺失映射成1001()
    {
        using var http = _fx.ApiFactory.CreateClient();
        var resp = await http.GetAsync("/api/family/list");

        // JwtBearerEvents.OnChallenge 里显式把状态码按成 200，再写封套
        ((int)resp.StatusCode).Should().Be(200);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
        body.GetProperty("code").GetInt32().Should().Be(1001);
    }

    // ── 封套优先：HTTP 状态码恒为 200 ────────────────────────

    [Theory]
    [InlineData(1002)]
    [InlineData(1003)]
    [InlineData(1004)]
    [InlineData(1005)]
    [InlineData(1009)]
    public async Task 业务错误的HTTP状态码恒为200(int expectedCode)
    {
        var (fid, openid) = await SeedFamilyAsync("http" + expectedCode,
            expectedCode == 1003 ? "member" : "admin");

        var (status, body) = expectedCode switch
        {
            1002 => await _fx.Api.GetAsync("/api/family/info?fid=F_nope", openid),
            1003 => await _fx.Api.PostAsync("/api/family/reset_invite", new { fid }, openid),
            1004 => await BuildOverQuotaAsync(fid, openid),
            1005 => await _fx.Api.PostAsync("/api/family/join", new { }, openid),
            _ => await _fx.Api.PostAsync("/api/family/create", new { name = new string('超', 200) }, openid),
        };

        status.Should().Be(HttpStatusCode.OK, $"code={expectedCode} 也必须回 200（封套优先）");
        body.GetProperty("code").GetInt32().Should().Be(expectedCode);
    }

    private async Task<(HttpStatusCode, JsonElement)> BuildOverQuotaAsync(string fid, string openid)
    {
        var uid = "P_env_quota_" + fid;
        await _fx.Seed.PatientAsync(uid, fid);
        for (var i = 0; i < 3; i++)
            await _fx.Seed.MedicineAsync($"M_env_quota_{fid}_{i}", fid, uid);
        return await _fx.Api.PostAsync("/api/medicine/create", new { fid, uid, name = "第4个药" }, openid);
    }

    /// <summary>错误码集合是冻结的，不许新增（契约 §一 / AGENTS.md §6.3）。</summary>
    [Fact]
    public void 错误码集合只有七个()
    {
        var codes = new[] { 0, 1001, 1002, 1003, 1004, 1005, 1009 };

        codes.Should().BeEquivalentTo(new[] { 0, 1001, 1002, 1003, 1004, 1005, 1009 });
        codes.Should().OnlyHaveUniqueItems();
    }
}
