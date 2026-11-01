namespace MedBox.Application.Abstractions;

/// <summary>
/// dose_logs 一行用于状态 CAS 判定的最小快照：只有这三项决定"能不能改"。
/// 用投影而不是实体，避免把跟踪实体留在上下文里被 SaveChanges 顺手写回去（那就是读改写覆盖）。
/// </summary>
public sealed record DoseSnapshot(string Status, bool IsCorrected, long DueTs);
