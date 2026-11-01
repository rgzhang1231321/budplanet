using MedBox.Domain;

namespace MedBox.Application.Abstractions;

/// <summary>
/// dose_logs 人工改判的 CAS 请求：只有"当前状态 == <see cref="From"/> 且未被改判"时才写入。
/// 与打卡 CAS 的差别：改判要盖 checked_by / checked_at / is_corrected，
/// 并**刻意不动 taken_at** —— 改判成 missed 是"这条记错了"，不是"他当时没吃"，
/// 原始打卡时间抹掉就再也说不清。
/// </summary>
public sealed record DoseCorrectUpdate(
    string DoseId,
    string FamilyId,
    DoseStatus From,
    DoseStatus To,
    string OperatorOpenid,
    long NowMs);
