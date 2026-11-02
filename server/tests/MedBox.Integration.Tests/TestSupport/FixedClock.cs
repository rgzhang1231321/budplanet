using MedBox.Application.Abstractions;

namespace MedBox.Integration.Tests.TestSupport;

/// <summary>固定时钟：物化用例要一个确定的"今天"，不能跟着真实时间跑。</summary>
public sealed class FixedClock(long nowMs, string todayCn) : IClock
{
    public long NowMs { get; } = nowMs;

    public string TodayCn { get; } = todayCn;
}
