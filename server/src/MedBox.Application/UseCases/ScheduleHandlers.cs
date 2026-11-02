using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using MedBox.Application.Services;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MedBox.Application.UseCases;

/// <summary>
/// schedule.today：老人端与子女端同一接口，一次给全 today 树（含已过点）。
/// 数据视角判 **role**（admin/member 看全家、elder 只看自己），在服务端裁 —— 不是下发后前端隐藏。
/// 为什么不用 IsElder（= 有没有自绑服药人）裁：契约 §4.1 mode 只管渲染、role 只管权限，
/// 而 role='admin' + 自绑（子女自己也吃降压药、同时管全家）是常见组合，用自绑裁数据
/// 会把这类人的爸妈整片裁掉（"爸爸的记录显示成我的"就是这条链路）。
/// </summary>
public sealed class TodayHandler(IAppDbContext db, IMemberGuard guard, DoseMaterializer materializer, IClock clock, ILogger<TodayHandler> logger)
{
    public async Task<TodayResultDto> HandleAsync(string fid, string date, string openid, CancellationToken ct = default)
    {
        var m = await guard.AssertMemberAsync(fid, openid, ct);
        if (string.IsNullOrWhiteSpace(date))
        {
            date = clock.TodayCn;
        }
        else if (!TimeCn.TryParseDate(date, out date))
        {
            // 契约：参数错误是 1005；让它抛 FormatException 会变 1009，前端当成服务异常无限重投
            throw new ValidationException("date 格式应为 YYYY-MM-DD（东八区自然日）");
        }

        await materializer.EnsureDayAsync(fid, date, ct);

        var q = db.DoseLogs.Where(d => d.FamilyId == fid && d.DueDate == date);
        // ★ 判据只看 role，不看"有没有 selfUid"：role=elder 就是被照护方，他手上没有 pid
        // 时（selfUids 空）`Contains` 恒 false → 返回空药单，这是**正确的从严**。
        // 旧写法带 `&& SelfUids.Length > 0` 是为了"别让老人看到空药单"，代价是 self 没绑上时
        // 不过滤 → 全家药名下发到老人手机（2026-11-02 事故的老人端同形）。根因已在
        // family.join 的新成员分支补上（进家即绑 self），所以这里不再留那个后门。
        // 空药单的前端口径：老人端显示「还没绑上你的服药人，让家人帮你绑一下」，不说"没有药"。
        if (m.Role == "elder") q = q.Where(d => m.SelfUids.Contains(d.PatientId));

        var rows = await q.OrderBy(d => d.Slot).ToListAsync(ct);
        var byPatient = rows.GroupBy(r => r.PatientId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Slot).ToList());

        // P1-2：用药提示（饭前/饭后/随餐）跟着行下发，打卡卡片才能显示标签。
        // 这是展示信息，读药品当前备注即可；不落快照（历史药名/剂量另有快照，备注不需要）。
        var medIds = rows.Select(r => r.MedicineId).Distinct().ToList();
        var medNotes = await db.Medicines
            .Where(m => medIds.Contains(m.MedicineId))
            .Select(m => new { m.MedicineId, m.Remark })
            .ToDictionaryAsync(x => x.MedicineId, x => x.Remark, ct);

        var dtos = rows.Select(r =>
        {
            var siblings = byPatient[r.PatientId];
            var index = siblings.FindIndex(x => x.DoseId == r.DoseId) + 1;
            return new DoseRowDto
            {
                DoseId = r.DoseId,
                Pid = r.PatientId,
                Slot = r.Slot,
                MedicineName = r.MedNameSnapshot ?? "",
                DoseText = r.DoseText ?? "",
                DueTime = TimeCn.HhMm(r.Slot),
                DueTs = r.DueTs,
                Status = r.Status,
                IsLate = r.IsLate,
                TakenAt = r.TakenAt ?? 0,
                IsCorrected = r.IsCorrected,
                SnoozeCount = r.SnoozeCount,
                TodayIndex = index,
                TodayTotal = siblings.Count,
                Note = medNotes.TryGetValue(r.MedicineId, out var note) ? note : null,
            };
        }).ToList();

        // 子女打开看板 = 一次"我来看过"；纯服药人不记（自己看自己的药单，写了也没人读）。
        // 判 role：自绑的 admin 也是"来看过"，他的回执对爸妈同样有意义。
        if (m.Role != "elder") await RecordViewAsync(fid, openid, m.Role, ct);

        return new TodayResultDto
        {
            Rows = dtos,
            // 安抚回执只发给纯服药人：与前端 home 的 recentText 同源（pureElderView）
            RecentView = m.Role == "elder" ? await RecentViewAsync(fid, openid, ct) : null,
            ServerTs = clock.NowMs,
        };
    }

    /// <summary>
    /// 已读回执出参：取"昨天、非本人、非 elder"的最近一条。
    /// 排掉 elder 是产品语义 —— 同龄人看不看，不构成"有人在意我"；排掉本人是废话。
    /// </summary>
    private async Task<RecentViewDto?> RecentViewAsync(string fid, string openid, CancellationToken ct)
    {
        var todayStart = TimeCn.DayStartUtcMs(clock.TodayCn);
        var yesterdayStart = todayStart - 86_400_000L;

        var row = await db.DoseViews
            .Where(v => v.FamilyId == fid
                        && v.ViewedAt >= yesterdayStart && v.ViewedAt < todayStart
                        && v.ViewerOpenid != openid
                        && v.ViewerRole != "elder")
            .OrderByDescending(v => v.ViewedAt)
            .FirstOrDefaultAsync(ct);

        return row is null ? null : new RecentViewDto { Alias = row.ViewerAlias ?? "家人", At = row.ViewedAt };
    }

    /// <summary>
    /// 记一条"我看了这个家庭"。只有子女端记（老人端自己看自己的药单，写进来也没有人会读）。
    /// view_id = openid_日期 幂等：一天一条、更新为当天最后一次，避免每次下拉刷新都插一行。
    /// 写失败**不能**影响 today —— 回执是锦上添花，看板才是主链路；但要留日志，
    /// 否则 dose_views 一直空着、看板永远没有"XX 昨天看过"，也查不出是谁拦住的。
    /// </summary>
    private async Task RecordViewAsync(string fid, string openid, string role, CancellationToken ct)
    {
        try
        {
            var today = clock.TodayCn;
            var viewId = $"{openid}_{today}";
            var nick = await db.Users.Where(u => u.Openid == openid).Select(u => u.NickName).FirstOrDefaultAsync(ct);
            var alias = string.IsNullOrWhiteSpace(nick) ? "家人" : nick!;

            var existing = await db.DoseViews.FirstOrDefaultAsync(v => v.ViewId == viewId, ct);
            if (existing is null)
            {
                db.Add(new DoseView
                {
                    ViewId = viewId,
                    FamilyId = fid,
                    ViewerOpenid = openid,
                    ViewerRole = role,
                    ViewerAlias = alias,
                    ViewedAt = clock.NowMs,
                });
            }
            else
            {
                existing.ViewedAt = clock.NowMs;
                existing.ViewerRole = role;
                existing.ViewerAlias = alias;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 故意不向上抛（让它把看板打挂是本末倒置），但必须留痕
            logger.LogWarning(ex, "已读回执写入失败，不影响今日药单 fid={Fid} viewId={ViewId}", fid, $"{openid}_{clock.TodayCn}");
        }
    }
}

/// <summary>
/// schedule.week：完成率只算「系统判定 + 自主打卡」，人工改判单列、不进分子分母。
/// </summary>
public sealed class WeekHandler(IAppDbContext db, IMemberGuard guard, DoseMaterializer materializer, IClock clock)
{
    public async Task<WeekResultDto> HandleAsync(string fid, string? weekStart, string openid, CancellationToken ct = default)
    {
        var m = await guard.AssertMemberAsync(fid, openid, ct);

        string start;
        if (string.IsNullOrWhiteSpace(weekStart))
        {
            start = TimeCn.WeekStart(clock.TodayCn);
        }
        else if (!TimeCn.TryParseDate(weekStart, out start))
        {
            throw new ValidationException("weekStart 格式应为 YYYY-MM-DD（东八区自然日）");
        }

        var dates = TimeCn.WeekDates(start).ToList();

        // 整周一次补齐（3 次往返），不再逐天 EnsureDayAsync
        await materializer.EnsureDaysAsync(fid, dates, ct);

        var q = db.DoseLogs.Where(x => x.FamilyId == fid && dates.Contains(x.DueDate));
        // 同 TodayHandler：数据视角判 role，不判自绑（契约 §4.1），空 selfUids 时返回空而不是放行。
        if (m.Role == "elder") q = q.Where(x => m.SelfUids.Contains(x.PatientId));
        var rows = await q.ToListAsync(ct);

        // 分母 = 计划次数（不含人工改判行）；分子 = 自主完成
        var countable = rows.Where(x => !x.IsCorrected).ToList();
        var taken = countable.Count(x => x.Status == DoseStatusWire.Taken);
        var manual = rows.Count(x => x.IsCorrected);
        var rate = countable.Count == 0 ? 0 : (int)Math.Round(taken * 100.0 / countable.Count);

        var patientIds = rows.Select(x => x.PatientId).Distinct().ToList();
        var patients = await db.Patients.Where(p => patientIds.Contains(p.PatientId)).ToListAsync(ct);

        var missList = rows
            .Where(x => x.Status == DoseStatusWire.Missed)
            .OrderBy(x => x.DueDate).ThenBy(x => x.Slot)
            .Select(x => new MissItemDto
            {
                Date = x.DueDate,
                Slot = x.Slot,
                Pid = x.PatientId,
                Patient = patients.FirstOrDefault(p => p.PatientId == x.PatientId)?.Alias ?? "",
                MedicineName = x.MedNameSnapshot ?? "",
                DoseText = x.DoseText ?? "",
            })
            .ToList();

        return new WeekResultDto
        {
            Rate = rate,
            FamilyCount = rows.Count(x => x.Status == DoseStatusWire.Taken),
            ManualCorrectCount = manual,
            MissList = missList,
            ServerTs = clock.NowMs,
        };
    }
}
