namespace MedBox.Application.Abstractions;

/// <summary>应用时钟。所有 created_at / due_ts 由它生成，便于 FakeTimeProvider 测试。</summary>
public interface IClock
{
    /// <summary>UTC 毫秒。</summary>
    long NowMs { get; }

    /// <summary>东八区自然日 YYYY-MM-DD。</summary>
    string TodayCn { get; }
}
