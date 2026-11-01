using MedBox.Application.Abstractions;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.Services;

/// <summary>
/// 激励视频权益（无现金支付）。product_id：
///   MED_SLOT       —— 额外药品名额，永久、按名额累加
///   WEEK_REPORT    —— 某周详细报告，按周归档
/// 前端只渲染 granted，权益一律以后端为准。
/// </summary>
public sealed class EntitlementService(IAppDbContext db, ConfigReader config, IClock clock)
{
    public const string MedSlot = "MED_SLOT";
    public const string WeekReport = "WEEK_REPORT";

    /// <summary>家庭可用药品名额 = 免费额度 + 已解锁名额。</summary>
    public async Task<int> MedSlotCreditsAsync(string familyId, CancellationToken ct = default)
        => await db.Entitlements
            .Where(e => e.FamilyId == familyId && e.ProductId == MedSlot)
            .SumAsync(e => (int?)e.Credits, ct) ?? 0;

    /// <summary>登录下发的 credits：该用户名下所有家庭的 MED_SLOT 名额合计。</summary>
    public async Task<int> UserCreditsAsync(string openid, CancellationToken ct = default)
    {
        var fids = await db.FamilyMembers.Where(m => m.Openid == openid).Select(m => m.FamilyId).ToListAsync(ct);
        if (fids.Count == 0) return 0;
        return await db.Entitlements
            .Where(e => fids.Contains(e.FamilyId) && e.ProductId == MedSlot)
            .SumAsync(e => (int?)e.Credits, ct) ?? 0;
    }

    public async Task<AdUnlockResult> GrantByAdAsync(string productId, string adTrace, string openid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adTrace)) throw new ValidationException("缺少 ad_trace");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Openid == openid, ct)
                   ?? throw new ValidationException("请先登录");
        var fid = !string.IsNullOrEmpty(user.ActiveFamilyId)
            ? user.ActiveFamilyId
            : (user.FamilyIds.Length > 0 ? user.FamilyIds[0] : throw new ValidationException("还没有家庭"));

        // 1) 幂等：同一个 ad_trace 重放直接返回（微信激励视频可能重复回调）
        var dup = await db.AdEvents.AsNoTracking().FirstOrDefaultAsync(e => e.AdTrace == adTrace, ct);
        if (dup is not null)
            return new AdUnlockResult(true, await MedSlotCreditsAsync(fid, ct), 0, Replay: true);

        var cfg = await config.GetAsync(ct);
        var now = clock.NowMs;
        var dayStart = TimeCn.DayStartUtcMs(clock.TodayCn);

        // 2) 冷却窗：同一用户同一场景 N 秒内只能核销一次（N = config.interstitialCool）
        var lastGrantAt = await db.AdEvents
            .AsNoTracking()
            .Where(e => e.Openid == openid && e.ProductId == productId)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => (long?)e.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (AdUnlockPolicy.InCooldown(now, lastGrantAt, cfg.InterstitialCool))
            throw new QuotaExceededException($"刚领过，{cfg.InterstitialCool} 秒后再试");

        // 3) 每场景日上限（= config.interstitialDayMax）
        var todayCount = await db.AdEvents
            .CountAsync(e => e.Openid == openid && e.ProductId == productId && e.CreatedAt >= dayStart, ct);
        if (AdUnlockPolicy.ExceedsDayMax(todayCount, cfg.InterstitialDayMax))
            throw new QuotaExceededException("今天这个解锁次数用完了");

        // 4) 幂等写入：并发重复回调撞 ad_trace 主键时 DO NOTHING；
        //    0 行 = 另一个请求已经核销过，按幂等结果返回，绝不抛异常变成 1009
        var inserted = await db.InsertAdEventIgnoreConflictAsync(new AdEvent
        {
            AdTrace = adTrace,
            Openid = openid,
            FamilyId = fid,
            ProductId = productId,
            Granted = true,
            CreatedAt = now,
        }, ct);
        if (inserted == 0)
            return new AdUnlockResult(true, await MedSlotCreditsAsync(fid, ct), 0, Replay: true);

        db.Add(new Entitlement
        {
            EntitlementId = "E" + Guid.NewGuid().ToString("N")[..12],
            FamilyId = fid,
            Openid = openid,
            ProductId = productId,
            Credits = 1,
            Source = "ad",
            ExpireAt = null,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);

        return new AdUnlockResult(true, await MedSlotCreditsAsync(fid, ct), 0, Replay: false);
    }
}

public sealed record AdUnlockResult(bool Granted, int Credits, long ExpireAt, bool Replay);
