using System.Text.Json;
using MedBox.Application.Abstractions;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.Services;

/// <summary>
/// 推送任务入队（削峰）。模板 ID 未配置时**一条都不建** —— 造出来的任务只能失败，
/// 既污染队列又会让 remind_status 说谎。
/// </summary>
public sealed class PushTaskBuilder(IAppDbContext db, IClock clock, ConfigReader config)
{
    /// <summary>只补发"刚过点"的提醒：过时的提醒没有意义，漏服告警会兜底。</summary>
    private const long RemindWindowMs = 10 * 60_000L;

    public async Task<int> EnqueueDueRemindersAsync(CancellationToken ct = default)
    {
        var cfg = await config.GetAsync(ct);
        var tmplId = cfg.TmplIds.Checkin;
        if (string.IsNullOrWhiteSpace(tmplId)) return 0;

        var now = clock.NowMs;
        var today = clock.TodayCn;

        var rows = await db.DoseLogs
            .Where(d => d.DueDate == today
                        && d.Status == DoseStatusWire.Pending
                        && d.RemindStatus == 0
                        && d.DueTs <= now
                        && d.DueTs > now - RemindWindowMs)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        // 只有在患者本人绑定了 openid 时才推给他（推给子女是另一条链路：漏服告警）
        var pids = rows.Select(r => r.PatientId).Distinct().ToList();
        var patients = await db.Patients.Where(p => pids.Contains(p.PatientId)).ToListAsync(ct);

        var created = 0;
        foreach (var row in rows)
        {
            var patient = patients.FirstOrDefault(p => p.PatientId == row.PatientId);
            var openid = patient?.SelfOpenid;
            if (string.IsNullOrEmpty(openid)) continue; // 没人可推，保持 remind_status=0

            created += await EnqueueAsync(
                openid,
                "CHECKIN",
                new
                {
                    alias = patient?.Alias ?? patient?.Name ?? "家人",
                    due_time = TimeCn.HhMm(row.Slot),
                    dose_text = row.DoseText ?? "",
                },
                priority: 0,
                ct);

            row.RemindStatus = 1; // 已入队（发送结果记在 push_tasks 上）
            row.RemindAt = now;
        }

        await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>漏服告警：推给家庭管理员，payload 不带药品名（模板落锁屏，可能被旁人看到）。</summary>
    public async Task<int> EnqueueMissedAlertsAsync(CancellationToken ct = default)
    {
        var cfg = await config.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(cfg.TmplIds.Missed)) return 0;

        var now = clock.NowMs;
        var fromTs = TimeCn.DayStartUtcMs(TimeCn.DateCn(now - 86_400_000L));

        var rows = await db.DoseLogs
            .Where(d => d.Status == DoseStatusWire.Missed && d.AlertStatus == 0 && d.DueTs >= fromTs)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        var fids = rows.Select(r => r.FamilyId).Distinct().ToList();
        var admins = await db.FamilyMembers
            .Where(m => fids.Contains(m.FamilyId) && m.Role == "admin" && m.NotifyEnabled)
            .ToListAsync(ct);
        var pids = rows.Select(r => r.PatientId).Distinct().ToList();
        var patients = await db.Patients.Where(p => pids.Contains(p.PatientId)).ToListAsync(ct);

        var created = 0;
        foreach (var row in rows)
        {
            var admin = admins.FirstOrDefault(m => m.FamilyId == row.FamilyId);
            if (admin is null) continue;

            var patient = patients.FirstOrDefault(p => p.PatientId == row.PatientId);
            created += await EnqueueAsync(
                admin.Openid,
                "MISSED",
                new { alias = patient?.Alias ?? "家人", due_time = TimeCn.HhMm(row.Slot) },
                priority: 1,
                ct);

            row.AlertStatus = 1;
            row.AlertAt = now;
        }

        await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>
    /// 周报推送：归档完成后给**每个家庭的管理员**入队一条，payload 带 stat_time（周区间）+ summary（完成率/漏服次数）。
    /// 一个家庭一条而不是「每个服药人一条」：一次授权只有一条额度，多服药人家庭按人发会瞬间吃光额度，
    /// 且周报页本身就是家庭级汇总，拆开发与页面口径也不一致。
    /// </summary>
    public async Task<int> EnqueueWeeklyReportsAsync(string weekKey, string weekRange, CancellationToken ct = default)
    {
        var cfg = await config.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(cfg.TmplIds.Weekly)) return 0;

        var stats = await db.WeeklyStats.Where(w => w.WeekKey == weekKey).ToListAsync(ct);
        if (stats.Count == 0) return 0;

        var fids = stats.Select(s => s.FamilyId).Distinct().ToList();
        var admins = await db.FamilyMembers
            .Where(m => fids.Contains(m.FamilyId) && m.Role == "admin" && m.NotifyEnabled)
            .ToListAsync(ct);

        var created = 0;
        foreach (var g in stats.GroupBy(s => s.FamilyId))
        {
            var total = g.Sum(s => s.Total);
            if (total == 0) continue; // 整周一口没记录，推一条"完成率0%"只是吓人，不发

            var admin = admins.FirstOrDefault(m => m.FamilyId == g.Key);
            if (admin is null) continue;

            var taken = g.Sum(s => s.TakenCnt);
            var missed = g.Sum(s => s.MissedCnt);

            created += await EnqueueAsync(
                admin.Openid,
                "WEEKLY",
                new { stat_time = weekRange, summary = WeeklySummary.Build(taken, total, missed) },
                priority: 2,
                ct,
                dedupeScope: weekKey);
        }

        await db.SaveChangesAsync(ct);
        return created;
    }

    private async Task<int> EnqueueAsync(
        string openid,
        string tmplKey,
        object payload,
        short priority,
        CancellationToken ct,
        string? dedupeScope = null)
    {
        var now = clock.NowMs;
        var taskId = dedupeScope is null
            ? PushPolicy.TaskId(openid, now / 60_000, tmplKey)
            : PushPolicy.ScopedTaskId(openid, dedupeScope, tmplKey);

        // 幂等靠 ON CONFLICT：返回 0 行 = 这条已经在队列里（同一分钟桶 / 同一周报周）。
        // 原来先 AnyAsync 再 Add，两个 tick 同时到就撞主键抛 1009，而任务其实已经排上了。
        return await db.InsertPushTaskIgnoreConflictAsync(new PushTask
        {
            TaskId = taskId,
            Shard = PushPolicy.Shard(openid),
            Openid = openid,
            TmplKey = tmplKey,
            Status = "queued",
            Priority = priority,
            Payload = JsonSerializer.Serialize(payload),
            RetryCount = 0,
            NextRetryAt = 0,
            CreatedAt = now,
        }, ct);
    }
}

/// <summary>
/// 模板字段映射：字段名以「我的模板」里实际分配的序号为准，换模板时只改这一个文件。
/// 当前三套（2026-10-30 选用，公共模板库）：
///   CHECKIN → 药剂更换提醒   (编号 10596)：服药剂量 = short_thing7(≤5字) · 服药时间 = time6
///   MISSED  → 工作任务待办通知 (编号 49925)：处理人 = thing18 · 时间 = time3 · 办理事项 = thing14
///   WEEKLY  → 周报更新提醒   (编号 1542)：统计时间 = time5 · 备注 = thing6(≤20字)
/// 长度限制：thing ≤20 字符、name ≤10 汉字、short_thing ≤5 字符；time 用 HH:mm 或短区间。
/// 注意：CHECKIN 的 short_thing7 仅 5 字，只能放剂量（如「1片」），药品名称放不下——这是该模板的容量上限。
/// </summary>
public static class TemplateData
{
    public static Dictionary<string, string> For(string tmplKey, JsonElement payload)
    {
        string S(string name) => payload.TryGetProperty(name, out var v) ? (v.GetString() ?? "") : "";
        static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        var alias = Cut(S("alias"), 10);
        var time = S("due_time");

        return tmplKey switch
        {
            // CHECKIN：服药剂量(short_thing7) + 服药时间(time6)
            "CHECKIN" => new Dictionary<string, string>
            {
                ["short_thing7"] = Cut(S("dose_text"), 5),
                ["time6"] = time,
            },
            // MISSED：处理人(thing18) + 时间(time3) + 办理事项(thing14)
            "MISSED" => new Dictionary<string, string>
            {
                ["thing18"] = alias,
                ["time3"] = time,
                ["thing14"] = "还没记录用药",
            },
            // WEEKLY：统计时间(time5) + 备注(thing6)。入队 payload 须含 stat_time、summary 两键
            "WEEKLY" => new Dictionary<string, string>
            {
                ["time5"] = S("stat_time"),
                ["thing6"] = Cut(S("summary"), 20),
            },
            _ => new Dictionary<string, string>(),
        };
    }
}
