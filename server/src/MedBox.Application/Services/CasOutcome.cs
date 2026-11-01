using MedBox.Domain;

namespace MedBox.Application.Services;

/// <summary>
/// 单条打卡的 CAS 结果：要么已落库（<see cref="Changed"/> 区分真写入 / 幂等空操作），
/// 要么被拒（<see cref="RejectCode"/> 1005 / 1003）。
/// </summary>
public sealed record CasOutcome
{
    /// <summary>这一条在库里是否存在（不存在是 1005「这一条不存在」）。</summary>
    public bool Exists { get; private init; }
    public string? Status { get; private init; }
    public bool Changed { get; private init; }
    public int? RejectCode { get; private init; }
    public string? RejectMsg { get; private init; }

    public bool IsRejected => RejectCode is not null;

    public static CasOutcome Done(DoseStatus to, bool changed) =>
        new() { Exists = true, Status = to.ToWire(), Changed = changed };

    public static CasOutcome Missing() =>
        new() { Exists = false, RejectCode = 1005, RejectMsg = "这一条不存在" };

    public static CasOutcome Reject(int code, string msg) =>
        new() { Exists = true, RejectCode = code, RejectMsg = msg };
}
