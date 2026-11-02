using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.UseCases;

/// <summary>
/// checkin.submit：天生批量（1–8 条），幂等键 pid_date_slot，部分成功合法、不整批回滚。
/// 注意入参没有 fid：家庭从 pid 反查，且必须唯一。
/// 写入走 <see cref="DoseCasApplier"/>（带状态守卫的 CAS），不再"读实体 → 改字段 → SaveChanges"。
/// </summary>
public sealed class SubmitHandler(IAppDbContext db, IMemberGuard guard, IClock clock, DoseCasApplier cas)
{
    public const int BatchMax = 8;

    public async Task<SubmitResultDto> HandleAsync(SubmitRequest req, string openid, CancellationToken ct = default)
    {
        var items = req.Items ?? [];
        if (items.Count == 0) throw new ValidationException("没有要提交的记录");
        if (items.Count > BatchMax) throw new ValidationException($"单批最多 {BatchMax} 条");

        var pids = items.Select(i => i.Pid).Distinct().ToList();
        var patients = await db.Patients.Where(p => pids.Contains(p.PatientId)).ToListAsync(ct);
        var fids = patients.Select(p => p.FamilyId).Distinct().ToList();
        if (patients.Count == 0) throw new ValidationException("服药人不存在");
        if (fids.Count > 1) throw new ValidationException("一次只能提交同一个家庭的记录");

        var m = await guard.AssertMemberAsync(fids[0], openid, ct);

        var applied = new List<AppliedItem>();
        var rejected = new List<RejectedItem>();
        var now = clock.NowMs;

        foreach (var item in items)
        {
            if (!DoseStatusWire.TryParse(item.Status, out var to) || to == DoseStatus.Pending)
            {
                rejected.Add(Reject(item, 1005, "状态不合法"));
                continue;
            }
            if (!patients.Any(p => p.PatientId == item.Pid))
            {
                rejected.Add(Reject(item, 1005, "服药人不存在"));
                continue;
            }
            // 判据是 **role**（纯服药人只能记自己的药），不是 IsElder（= 有没有自绑服药人）。
            // 两者只在"只有老人自己"的家庭里恰好等价；一旦是 role=admin/member 且自己也吃药
            // （契约 §4.1 点名的常见组合），IsElder 就恒为 true —— schedule.today 已经把全家的
            // 药下发给这位子女了（按 role 裁），他到这儿给爸妈打卡会吃 1003「只能记自己的药」，
            // 表现为"看得见、点不动"，且没有任何前端文案能解释。
            if (m.Role == "elder" && !m.SelfUids.Contains(item.Pid))
            {
                await guard.AuditForbiddenAsync(fids[0], openid, "checkin.submit", ct);
                rejected.Add(Reject(item, 1003, "只能记自己的药"));
                continue;
            }

            // CAS 写入：0 行说明状态被并发改过（漏服判定抢先），由 applier 重读真实状态决定结果，
            // 绝不因为 0 行就静默算成功 —— 那正是"用户点了已服、库里是 missed"的成因
            var outcome = await cas.ApplyAsync(
                DoseId.Build(item.Pid, item.Date, item.Slot), fids[0], to, openid, item.SkipReason, ct);

            if (outcome.IsRejected)
            {
                rejected.Add(Reject(item, outcome.RejectCode!.Value, outcome.RejectMsg ?? "状态不允许"));
                continue;
            }

            applied.Add(Apply(item, to, now, outcome.Changed));
        }

        return new SubmitResultDto
        {
            Applied = applied,
            Rejected = rejected,
            Quota = await QuotaAsync(openid, ct),
            ServerTs = now,
        };
    }

    private static AppliedItem Apply(SubmitItem item, DoseStatus to, long ts, bool changed) => new()
    {
        Pid = item.Pid,
        Date = item.Date,
        Slot = item.Slot,
        Status = to.ToWire(),
        Ts = ts,
        Changed = changed,
    };

    private static RejectedItem Reject(SubmitItem item, int code, string msg) => new()
    {
        Pid = item.Pid,
        Date = item.Date,
        Slot = item.Slot,
        Code = code,
        Msg = msg,
    };

    public async Task<QuotaDto> QuotaAsync(string openid, CancellationToken ct)
    {
        var rows = await db.Subscriptions.Where(s => s.Openid == openid).ToListAsync(ct);
        int Get(string key) => rows.FirstOrDefault(r => r.TmplKey == key)?.Quota ?? 0;
        return new QuotaDto { Checkin = Get("CHECKIN"), Missed = Get("MISSED"), Weekly = Get("WEEKLY") };
    }
}

/// <summary>
/// checkin.snooze：due_ts += 15min，次数上限 2；
/// 仅允许在 slot ~ slot+snoozeDeadlineMin(45) 之间调用，超出 1005。
/// </summary>
public sealed class SnoozeHandler(IAppDbContext db, IMemberGuard guard, ConfigReader config, IClock clock)
{
    /// <summary>契约冻结：`due_ts += 15min`（契约表 §七 snooze 行），不是可调参数。</summary>
    private const long SnoozeStepMs = 15 * 60_000L;

    public async Task<SnoozeResultDto> HandleAsync(string doseId, string openid, CancellationToken ct = default)
    {
        var log = await db.DoseLogs.AsNoTracking().FirstOrDefaultAsync(d => d.DoseId == doseId, ct)
                  ?? throw new ValidationException("这一条不存在");

        var m = await guard.AssertMemberAsync(log.FamilyId, openid, ct);
        // 同 checkin.submit：权限判 role。IsElder 会把"自己也吃药的子女"当成纯服药人，
        // 于是他连替爸妈延后提醒都做不到（能看到、能点，但每次都是 1003）。
        if (m.Role == "elder" && !m.SelfUids.Contains(log.PatientId))
        {
            await guard.AuditForbiddenAsync(log.FamilyId, openid, "checkin.snooze", ct);
            throw new ForbiddenException();
        }

        if (log.SnoozeCount >= 2) throw new ValidationException("已经延后 2 次了");
        if (log.Status != DoseStatusWire.Pending) throw new ValidationException("这条已经记过了");

        var cfg = await config.GetAsync(ct);
        var slotTs = TimeCn.SlotUtcMs(log.DueDate, log.Slot);
        var now = clock.NowMs;
        if (now < slotTs || now > slotTs + cfg.SnoozeDeadlineMin * 60_000)
            throw new ValidationException("这个点快到最后时间了，先记一下");

        var nextDueTs = log.DueTs + SnoozeStepMs;
        var nextCount = log.SnoozeCount + 1;

        // CAS + 必须把 remind_status 归 0：
        // 推送入队查询硬性要求 remind_status = 0（PushTaskBuilder.EnqueueDueRemindersAsync），
        // 第一次提醒发完就是 1；不归零的话延后到点后永远查不到 → 第二次提醒永远不响。
        var rows = await db.DoseLogs
            .Where(d => d.DoseId == doseId && d.Status == DoseStatusWire.Pending && d.SnoozeCount == log.SnoozeCount)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.DueTs, nextDueTs)
                .SetProperty(d => d.SnoozeCount, nextCount)
                .SetProperty(d => d.RemindStatus, (short)0)
                .SetProperty(d => d.RemindAt, (long?)null)
                .SetProperty(d => d.RemindErrcode, (int?)null), ct);

        // 0 行 = 期间被并发改了（打卡 / 漏服判定抢先），不能假装延后成功
        if (rows == 0) throw new ValidationException("这条状态刚变了，刷新一下再试");

        return new SnoozeResultDto { Ok = true, DueTs = nextDueTs, SnoozeCount = nextCount };
    }
}

/// <summary>
/// checkin.correct：仅 admin/member（elder 一律 1003），目标状态只收 missed/skipped，不可逆，留 correct_logs。
/// 写入走 CAS（WHERE status=@from AND is_corrected=false）：读实体→改→SaveChanges 会和漏服判定抢同一行，
/// 后写覆盖先写，改判就可能在漏服判定的瞬间被吞掉。
/// </summary>
public sealed class CorrectHandler(IAppDbContext db, IMemberGuard guard, IClock clock)
{
    public async Task<CorrectResultDto> HandleAsync(string doseId, string toStatusWire, string? reason, string openid, CancellationToken ct = default)
    {
        // 契约 §7:140：to_status 取值域就是 missed / skipped，不含 taken；
        // 回 taken 是"我吃了"，走 checkin.submit（§7:149）。先挡这一层，连库都不用查。
        if (!DoseStatusWire.TryParse(toStatusWire, out var to) || !DoseTransitions.IsCorrectTarget(to))
            throw new ValidationException("只能改成漏服或跳过");

        var familyId = await db.DoseLogs
            .Where(d => d.DoseId == doseId)
            .Select(d => d.FamilyId)
            .FirstOrDefaultAsync(ct)
            ?? throw new ValidationException("这一条不存在");

        await guard.AssertMemberAsync(familyId, openid, ct, "admin", "member");

        var now = clock.NowMs;

        for (var attempt = 0; attempt < DoseCasPolicy.MaxAttempts; attempt++)
        {
            var snap = await db.GetDoseSnapshotAsync(doseId, familyId, ct)
                       ?? throw new ValidationException("这一条不存在");
            if (!DoseStatusWire.TryParse(snap.Status, out var from)) from = DoseStatus.Pending;

            if (DoseCasPolicy.DecideCorrect(from, to, snap.IsCorrected, attempt) != CasStep.Apply)
                throw new ValidationException(RejectReason(from, snap.IsCorrected));

            // 0 行 = 期间被并发改了（漏服判定 / 另一次改判），下一轮按真实状态再判；
            // 绝不能因为 0 行就返回 ok —— 那就是"改了但没改"的假成功
            var rows = await db.CompareAndSetDoseCorrectAsync(
                new DoseCorrectUpdate(doseId, familyId, from, to, openid, now), ct);
            if (rows == 0) continue;

            db.Add(new CorrectLog
            {
                LogId = Guid.NewGuid().ToString("N"),
                DoseId = doseId,
                FamilyId = familyId,
                OperatorOpenid = openid,
                FromStatus = from.ToWire(),
                ToStatus = to.ToWire(),
                Reason = reason,
                CreatedAt = now,
            });
            await db.SaveChangesAsync(ct);

            return new CorrectResultDto { Ok = true, IsCorrected = true };
        }

        throw new ValidationException("状态刚刚变了，刷新一下再试");
    }

    private static string RejectReason(DoseStatus from, bool isCorrected)
    {
        if (isCorrected) return "这条已经改过，不能再改";
        return from switch
        {
            DoseStatus.Pending => "这条还没记过，不能改判",
            DoseStatus.Missed => "系统判的漏服要救回已服，请用打卡补记",
            DoseStatus.Skipped => "这条已经标过跳过，不能再改",
            _ => "状态不允许",
        };
    }
}
