using MedBox.Application.Abstractions;
using MedBox.Domain;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Application.UseCases;

/// <summary>user.agree_privacy / user.agree_disclaimer：留痕，绑 openid（谁进来谁确认）。</summary>
public sealed class UserAgreeHandler(IAppDbContext db, IClock clock)
{
    public async Task<object> HandleAsync(string kind, string openid, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Openid == openid, ct)
                   ?? throw new ValidationException("请先登录");

        var now = clock.NowMs;
        if (kind == "privacy") user.PrivacyAgreedAt = now;
        else user.DisclaimerAgreedAt = now;

        await db.SaveChangesAsync(ct);
        return new { ok = true, acknowledgedAt = now };
    }
}
