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
/// </summary>
public sealed class SubmitHandler(IAppDbContext db, IMemberGuard guard, IClock clock)
{
    public const int BatchMax = 8;
    private const int LateToleranceMin = 15;

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
            if (m.IsElder && !m.SelfUids.Contains(item.Pid))
            {
                rejected.Add(Reject(item, 1003, "只能记自己的药"));
                continue;
            }

            var doseId = DoseId.Build(item.Pid, item.Date, item.Slot);
            var log = await db.DoseLogs.FirstOrDefaultAsync(d => d.DoseId == doseId && d.FamilyId == fids[0], ct);
            if (log is null)
            {
                rejected.Add(Reject(item, 1005, "这一条不存在"));
                continue;
            }

            if (!DoseStatusWire.TryParse(log.Status, out var from))
                from = DoseStatus.Pending;

            if (from == to)
            {
                applied.Add(Apply(item, to, now, changed: false));
                continue;
            }

            if (!DoseTransitions.CanSubmit(from, to, log.IsCorrected))
            {
                rejected.Add(Reject(item, 1005, "状态不允许"));
                continue;
            }

            log.Status = to.ToWire();
            if (to == DoseStatus.Taken)
            {
                log.TakenAt = now;
                log.TakenBy = openid;
                log.IsLate = now - log.DueTs > LateToleranceMin * 60_000;
                log.SkipReason = null;
            }
            else
            {
                log.TakenAt = null;
                log.TakenBy = null;
                log.IsLate = false;
                log.SkipReason = item.SkipReason;
            }

            applied.Add(Apply(item, to, now, changed: true));
        }

        await db.SaveChangesAsync(ct);

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
    public async Task<SnoozeResultDto> HandleAsync(string doseId, string openid, CancellationToken ct = default)
    {
        var log = await db.DoseLogs.FirstOrDefaultAsync(d => d.DoseId == doseId, ct)
                  ?? throw new ValidationException("这一条不存在");

        var m = await guard.AssertMemberAsync(log.FamilyId, openid, ct);
        if (m.IsElder && !m.SelfUids.Contains(log.PatientId)) throw new ForbiddenException();

        if (log.SnoozeCount >= 2) throw new ValidationException("已经延后 2 次了");
        if (log.Status != DoseStatusWire.Pending) throw new ValidationException("这条已经记过了");

        var cfg = await config.GetAsync(ct);
        var slotTs = TimeCn.SlotUtcMs(log.DueDate, log.Slot);
        var now = clock.NowMs;
        if (now < slotTs || now > slotTs + cfg.SnoozeDeadlineMin * 60_000)
            throw new ValidationException("这个点快到最后时间了，先记一下");

        log.DueTs += 15 * 60_000;
        log.SnoozeCount += 1;
        await db.SaveChangesAsync(ct);

        return new SnoozeResultDto { Ok = true, DueTs = log.DueTs, SnoozeCount = log.SnoozeCount };
    }
}

/// <summary>
/// checkin.correct：仅 admin/member（elder 一律 1003），不可逆，留 correct_logs。
/// 系统判的漏服可以救，人判的漏服不能翻。
/// </summary>
public sealed class CorrectHandler(IAppDbContext db, IMemberGuard guard, IClock clock)
{
    public async Task<CorrectResultDto> HandleAsync(string doseId, string toStatusWire, string? reason, string openid, CancellationToken ct = default)
    {
        var log = await db.DoseLogs.FirstOrDefaultAsync(d => d.DoseId == doseId, ct)
                  ?? throw new ValidationException("这一条不存在");

        await guard.AssertMemberAsync(log.FamilyId, openid, ct, "admin", "member");

        if (!DoseStatusWire.TryParse(log.Status, out var from)) from = DoseStatus.Pending;
        if (!DoseStatusWire.TryParse(toStatusWire, out var to)) throw new ValidationException("状态不合法");

        if (!DoseTransitions.CanCorrect(from, to, log.IsCorrected))
            throw new ValidationException(log.IsCorrected ? "这条已经改过，不能再改" : "状态不允许");

        var now = clock.NowMs;
        log.Status = to.ToWire();
        log.IsCorrected = true;
        log.CheckedBy = openid;
        log.CheckedAt = now;
        log.TakenAt = to == DoseStatus.Taken ? now : null;

        db.Add(new CorrectLog
        {
            LogId = Guid.NewGuid().ToString("N"),
            DoseId = log.DoseId,
            FamilyId = log.FamilyId,
            OperatorOpenid = openid,
            FromStatus = from.ToWire(),
            ToStatus = to.ToWire(),
            Reason = reason,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);
        return new CorrectResultDto { Ok = true, IsCorrected = true };
    }
}
