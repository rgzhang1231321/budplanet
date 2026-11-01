namespace MedBox.Application.Abstractions;

/// <summary>
/// dose_logs 一行用于状态 CAS 判定的最小快照：只有这些项决定"能不能改"和"算不算迟到"。
/// 用投影而不是实体，避免把跟踪实体留在上下文里被 SaveChanges 顺手写回去（那就是读改写覆盖）。
/// DueDate + Slot 是 is_late 的基准；这里**故意不含 due_ts** —— snooze 会把它往后推，
/// 放进来迟早有人拿它当迟到基线。
/// </summary>
public sealed record DoseSnapshot(string Status, bool IsCorrected, string DueDate, int Slot);
