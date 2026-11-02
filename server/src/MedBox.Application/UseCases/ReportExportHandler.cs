using MedBox.Application.Abstractions;
using MedBox.Application.Contracts;
using MedBox.Application.Services;
using MedBox.Domain;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.UseCases;

/// <summary>
/// report.export（v1.6）：近 N 天（默认 30）用药报告，给复诊时向医生说明服药情况。
/// 合规红线：不含诊断 / 建议 / 「医嘱」；完成率是「用户记录的统计」，不是依从性评估。
/// 口径与周报一致：分母 = 计划次数（排除人工改判行），分子 = 自主完成。
/// </summary>
public sealed class ReportExportHandler(IAppDbContext db, IMemberGuard guard, DoseMaterializer materializer, IClock clock)
{
    public async Task<ReportExportResult> HandleAsync(string fid, int? days, string openid, CancellationToken ct = default)
    {
        var m = await guard.AssertMemberAsync(fid, openid, ct);
        var n = days is >= 1 and <= 90 ? days.Value : 30;

        var end = clock.TodayCn;
        var endDate = DateTime.ParseExact(
            end, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var start = endDate.AddDays(-(n - 1)).ToString("yyyy-MM-dd");
        var dates = Enumerable.Range(0, n).Select(i => endDate.AddDays(-i).ToString("yyyy-MM-dd")).ToList();

        await materializer.EnsureDaysAsync(fid, dates, ct);

        var q = db.DoseLogs.Where(x => x.FamilyId == fid && dates.Contains(x.DueDate));
        // 数据视角判 role（见 TodayHandler summary）：自绑的 admin 导出的报告也必须含全家人，
        // 否则"给医生看的近 30 天报告"里只剩他自己那几行。
        if (m.Role == "elder" && m.SelfUids.Length > 0) q = q.Where(x => m.SelfUids.Contains(x.PatientId));
        var rows = await q.ToListAsync(ct);

        var countable = rows.Where(x => !x.IsCorrected).ToList();
        var taken = countable.Count(x => x.Status == DoseStatusWire.Taken);
        var rate = countable.Count == 0 ? 0 : (int)Math.Round(taken * 100.0 / countable.Count);
        var missCount = rows.Count(x => x.Status == DoseStatusWire.Missed);

        // 按药聚合：用法取该药在区间内出现过的 (slot, dose_text) 去重；完成率同样排除人工改判
        var meds = rows
            .GroupBy(x => x.MedicineId)
            .Select(g =>
            {
                var usage = string.Join(" · ", g
                    .Select(x => (Slot: x.Slot, Text: x.DoseText ?? ""))
                    .Distinct()
                    .OrderBy(x => x.Slot)
                    .Select(x => $"{TimeCn.HhMm(x.Slot)} {x.Text}".Trim()));
                var gCountable = g.Where(x => !x.IsCorrected).ToList();
                var gTaken = gCountable.Count(x => x.Status == DoseStatusWire.Taken);
                var gRate = gCountable.Count == 0 ? 0 : (int)Math.Round(gTaken * 100.0 / gCountable.Count);
                return new ReportMedDto
                {
                    Name = g.Select(x => x.MedNameSnapshot).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "",
                    Usage = usage,
                    Rate = gRate,
                };
            })
            .OrderByDescending(x => x.Rate)
            .ToList();

        return new ReportExportResult
        {
            Range = new ReportRangeDto { Start = start, End = end },
            Rate = rate,
            MissCount = missCount,
            MedCount = meds.Count,
            Meds = meds,
            ServerTs = clock.NowMs,
        };
    }
}
