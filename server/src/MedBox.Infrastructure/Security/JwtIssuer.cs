using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MedBox.Application.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MedBox.Infrastructure.Security;

public sealed class JwtOptions
{
    /// <summary>只从环境变量 JWT__SECRET / user-secrets 读，禁止硬编码、禁止进 git。</summary>
    public string Secret { get; set; } = "";
    public string Issuer { get; set; } = "medbox";
    public string Audience { get; set; } = "medbox-mp";
    public int AccessDays { get; set; } = 7;
    public int RefreshDays { get; set; } = 30;
}

public sealed class JwtIssuer(IOptions<JwtOptions> options) : IJwtIssuer
{
    private readonly JwtOptions _opt = options.Value;

    public string Issue(string openid) => Build(openid, _opt.AccessDays, "access");
    public string IssueRefresh(string openid) => Build(openid, _opt.RefreshDays, "refresh");

    private string Build(string openid, int days, string typ)
    {
        if (string.IsNullOrWhiteSpace(_opt.Secret) || _opt.Secret.Length < 32)
            throw new InvalidOperationException("JWT__SECRET 未配置或过短（至少 32 字符）");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, openid),
            new("typ", typ),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(days),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
