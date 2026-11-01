using MedBox.Domain;

namespace MedBox.Application.Abstractions;

/// <summary>
/// dose_logs 状态 CAS 更新请求：只有"当前状态 == <see cref="From"/> 且未被人工改判"时才写入。
/// 受影响行数就是 CAS 的结果：1 = 真的改了；0 = 被别人抢先改过（漏服判定 / 人工改判）。
/// </summary>
public sealed record DoseCasUpdate(
    string DoseId,
    string FamilyId,
    DoseStatus From,
    DoseStatus To,
    string OperatorOpenid,
    long NowMs,
    long DueTs,
    bool IsLate,
    string? SkipReason);
