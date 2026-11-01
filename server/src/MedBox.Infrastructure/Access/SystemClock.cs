using MedBox.Application.Abstractions;
using MedBox.Domain;

namespace MedBox.Infrastructure.Access;

/// <summary>系统时钟。走 TimeProvider，测试可换 FakeTimeProvider。</summary>
public sealed class SystemClock(TimeProvider time) : IClock
{
    public long NowMs => time.GetUtcNow().ToUnixTimeMilliseconds();

    public string TodayCn => TimeCn.DateCn(NowMs);
}
