using System.Net;
using System.Text;
using FluentAssertions;
using MedBox.Infrastructure.Wx;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MedBox.Application.Tests;

/// <summary>
/// WxTokenCache 并发只调一次微信（审计 §13.4 点名的第 4 项）。
///
/// 为什么要盯这条：WxApiClient 是 typed HttpClient（每次注入都是新实例），缓存挂在它身上等于没缓存。
/// 缓存一旦失效，每个 60s 的 tick 都重新取 token，一天 8600 次会触发微信侧限流，
/// 之后**所有提醒静默停发** —— 用户以为"今天没药"，实际是账号被限流。这个故障没有任何前台报错。
///
/// 三条口径：
///   ① 双检锁：N 个线程同时要 token，微信接口只被打 1 次；
///   ② 退避：刷新失败且旧 token 没真过期 → 沿用旧 token，且退避窗口内不再打微信；
///   ③ 没配 AppSecret → 直接返回 null，一个请求都不发。
/// </summary>
public sealed class WxTokenCacheTests
{
    private const string AppId = "wx_test_appid";
    private const string Secret = "test_app_secret";

    private static WxOptions NewOptions(string? secret = Secret) => new()
    {
        AppId = AppId,
        AppSecret = secret ?? "",
        ApiBase = "https://api.weixin.qq.com",
    };

    /// <summary>计数型假 HttpMessageHandler：记录被打的次数，可按脚本返回成功/失败。</summary>
    private sealed class CountingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Func<HttpResponseMessage> Respond { get; set; } = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Respond());
        }
    }

    private static HttpResponseMessage TokenOk(string token, int expiresIn = 7200)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"access_token\":\"{token}\",\"expires_in\":{expiresIn}}}",
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage TokenFailed()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"errcode\":40001,\"errmsg\":\"invalid credential\"}",
                Encoding.UTF8, "application/json"),
        };

    // ── ① 并发只调一次微信 ────────────────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(32)]
    public async Task N个线程同时取token微信接口只被打一次(int threads)
    {
        var handler = new CountingHandler(() => TokenOk("TOKEN_FRESH"));
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T00:00:00Z"));
        var cache = new WxTokenCache(Options.Create(NewOptions()), time, NullLogger<WxTokenCache>.Instance);
        using var http = new HttpClient(handler);

        // 让第一个（也是唯一一个）微信调用卡在"等待放行"上，
        // 这样其余线程必然全部排在 SemaphoreSlim 上，能真正验到双检那段逻辑。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Respond = () =>
        {
            gate.Task.Wait();
            return TokenOk("TOKEN_FRESH");
        };

        var started = new TaskCompletionSource[threads];
        var tasks = new Task<string?>[threads];
        for (var i = 0; i < threads; i++)
        {
            started[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var myStart = started[i];
            tasks[i] = Task.Run(async () =>
            {
                myStart.TrySetResult();
                return await cache.GetAsync(http);
            });
        }

        // 等所有线程都进入 GetAsync（已过 AppSecret 检查与首次快路径检查）
        await Task.WhenAll(started.Select(t => t.Task));
        await Task.Delay(150); // 给它们足够时间排到信号量上

        gate.TrySetResult();   // 放行唯一那次微信调用
        var results = await Task.WhenAll(tasks);

        handler.Calls.Should().Be(1, "双检锁生效，第二次进临界区的线程应该直接拿到刚刷好的 token");
        results.Should().AllBe("TOKEN_FRESH");
    }

    /// <summary>快路径：token 还在有效期内，连信号量都不进，更不打微信。</summary>
    [Fact]
    public async Task token未过期时重复获取不再打微信()
    {
        var handler = new CountingHandler(() => TokenOk("TOKEN_A"));
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T00:00:00Z"));
        var cache = new WxTokenCache(Options.Create(NewOptions()), time, NullLogger<WxTokenCache>.Instance);
        using var http = new HttpClient(handler);

        (await cache.GetAsync(http)).Should().Be("TOKEN_A");
        (await cache.GetAsync(http)).Should().Be("TOKEN_A");
        (await cache.GetAsync(http)).Should().Be("TOKEN_A");

        handler.Calls.Should().Be(1);
    }

    // ── ② 刷新失败沿用旧 token + 退避 ─────────────────────────

    /// <summary>
    /// 刷新失败不许让整条提醒链路停摆：旧 token 没真过期就继续用。
    /// 微信 expires_in=7200s，提前 300s 换新 —— 所以"过了 refreshAt、没过 hardExpireAt"
    /// 中间有 300 秒窗口，这段窗口里刷新失败必须沿用旧 token。
    /// </summary>
    [Fact]
    public async Task 刷新失败且旧token未硬过期时沿用旧token()
    {
        var start = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
        var time = new FakeTimeProvider(start);
        var handler = new CountingHandler(() => TokenOk("TOKEN_OLD"));
        var cache = new WxTokenCache(Options.Create(NewOptions()), time, NullLogger<WxTokenCache>.Instance);
        using var http = new HttpClient(handler);

        (await cache.GetAsync(http)).Should().Be("TOKEN_OLD");
        handler.Calls.Should().Be(1);

        // 推进到"该换新了但旧的还没真过期"：7200 - 300 = 6900s
        time.SetUtcNow(start.AddSeconds(6901));
        handler.Respond = () => TokenFailed();

        (await cache.GetAsync(http)).Should().Be("TOKEN_OLD");
        handler.Calls.Should().Be(2, "确实又打了一次微信，只是失败了");
    }

    /// <summary>退避窗口内不再打微信 —— 别让每 60s 的 tick 都去打一次，把失败放大成限流。</summary>
    [Fact]
    public async Task 刷新失败后退避窗口内不再打微信()
    {
        var start = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
        var time = new FakeTimeProvider(start);
        var handler = new CountingHandler(() => TokenOk("TOKEN_OLD"));
        var cache = new WxTokenCache(Options.Create(NewOptions()), time, NullLogger<WxTokenCache>.Instance);
        using var http = new HttpClient(handler);

        (await cache.GetAsync(http)).Should().Be("TOKEN_OLD");
        time.SetUtcNow(start.AddSeconds(6901));
        handler.Respond = () => TokenFailed();
        (await cache.GetAsync(http)).Should().Be("TOKEN_OLD");
        handler.Calls.Should().Be(2);

        // 30s 退避窗口内再来一次：直接拿旧 token，一次微信都不打
        time.SetUtcNow(start.AddSeconds(6901 + 29));
        (await cache.GetAsync(http)).Should().Be("TOKEN_OLD");
        handler.Calls.Should().Be(2);

        // 过了退避窗口：允许再试一次（仍然失败、仍然沿用旧 token）
        time.SetUtcNow(start.AddSeconds(6901 + 31));
        (await cache.GetAsync(http)).Should().Be("TOKEN_OLD");
        handler.Calls.Should().Be(3);
    }

    /// <summary>旧 token 已经真过期 + 刷新失败 → 返回 null（调用方据此判定"这条提醒发不出去"）。</summary>
    [Fact]
    public async Task 旧token已硬过期且刷新失败时返回null()
    {
        var start = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
        var time = new FakeTimeProvider(start);
        var handler = new CountingHandler(() => TokenOk("TOKEN_OLD", expiresIn: 60));
        var cache = new WxTokenCache(Options.Create(NewOptions()), time, NullLogger<WxTokenCache>.Instance);
        using var http = new HttpClient(handler);

        (await cache.GetAsync(http)).Should().Be("TOKEN_OLD");

        // expires_in=60s → hardExpireAt = start+60s；推到它之后
        time.SetUtcNow(start.AddSeconds(120));
        handler.Respond = () => TokenFailed();

        (await cache.GetAsync(http)).Should().BeNull();
        handler.Calls.Should().Be(2);
    }

    /// <summary>网络异常（不是取消）按"刷新失败"处理，沿用旧 token；取消必须往外抛。</summary>
    [Fact]
    public async Task 网络异常时沿用旧token()
    {
        var start = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
        var time = new FakeTimeProvider(start);
        var handler = new CountingHandler(() => TokenOk("TOKEN_OLD"));
        var cache = new WxTokenCache(Options.Create(NewOptions()), time, NullLogger<WxTokenCache>.Instance);
        using var http = new HttpClient(handler);

        (await cache.GetAsync(http)).Should().Be("TOKEN_OLD");
        time.SetUtcNow(start.AddSeconds(6901));
        handler.Respond = () => throw new HttpRequestException("connection reset");

        (await cache.GetAsync(http)).Should().Be("TOKEN_OLD");
    }

    // ── ③ 没配 AppSecret ─────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task 没配AppSecret时返回null且不发请求(string? secret)
    {
        var handler = new CountingHandler(() => TokenOk("TOKEN_X"));
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T00:00:00Z"));
        var cache = new WxTokenCache(Options.Create(NewOptions(secret)), time, NullLogger<WxTokenCache>.Instance);
        using var http = new HttpClient(handler);

        (await cache.GetAsync(http)).Should().BeNull();
        handler.Calls.Should().Be(0, "没配 secret 时连微信都不该问");
    }
}
