using MedBox.Application.Abstractions;
using MedBox.Domain;

namespace MedBox.Application.Services;

/// <summary>
/// 打卡写入的 CAS 编排（不含 SQL）：读快照 → 判规则 → 带状态守卫的 UPDATE → 0 行就重读再决定。
/// 与漏服判定 worker 抢同一行时，谁先写谁算、后者看真实状态决定（AlreadyDone / Reject），
/// 不会出现"用户点了已服，最终被覆盖成 missed"。
/// </summary>
public sealed class DoseCasApplier(IAppDbContext db, IClock clock, int lateToleranceMin)
{
    public async Task<CasOutcome> ApplyAsync(
        string doseId,
        string familyId,
        DoseStatus to,
        string openid,
        string? skipReason,
        CancellationToken ct = default)
    {
        var now = clock.NowMs;

        for (var attempt = 0; attempt < DoseCasPolicy.MaxAttempts; attempt++)
        {
            var snap = await db.GetDoseSnapshotAsync(doseId, familyId, ct);
            if (snap is null) return CasOutcome.Missing();

            if (!DoseStatusWire.TryParse(snap.Status, out var from)) from = DoseStatus.Pending;

            switch (DoseCasPolicy.Decide(from, to, snap.IsCorrected, attempt))
            {
                case CasStep.AlreadyDone:
                    return CasOutcome.Done(to, changed: false);
                case CasStep.Reject:
                    return CasOutcome.Reject(1005, RejectReason(from, snap.IsCorrected));
            }

            // 迟到基准是原定 slot（与漏服判定同源）：拿 snap.DueTs 比，snooze 一次就永远不算迟到
            var isLate = to == DoseStatus.Taken && LatePolicy.IsLate(snap.DueDate, snap.Slot, now, lateToleranceMin);
            var rows = await db.CompareAndSetDoseAsync(
                new DoseCasUpdate(doseId, familyId, from, to, openid, now, isLate, skipReason), ct);

            if (rows == 1) return CasOutcome.Done(to, changed: true);
            // 0 行：状态被并发改了（漏服判定抢先）→ 下一轮按重读到的真实状态再决定，绝不静默算成功
        }

        return CasOutcome.Reject(1005, "状态刚刚变了，刷新一下再试");
    }

    private static string RejectReason(DoseStatus from, bool isCorrected) => from switch
    {
        DoseStatus.Skipped => "当天标了跳过，不能改成已服",
        DoseStatus.Missed when isCorrected => "这条已经人工改判过，按规则不能再改",
        DoseStatus.Taken => "这条已经记过了",
        _ => "状态不允许",
    };
}
