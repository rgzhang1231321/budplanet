using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MedBox.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace MedBox.Infrastructure.Security;

/// <summary>
/// 导出链接签名：`base64url(payload).base64url(HMAC-SHA256(payload))`，payload = {"sub":openid,"exp":ms}。
/// 复用 JWT 的密钥（同一个信任根），但**不带 JWT 头**：它是给浏览器的预签名下载地址，
/// 不该被当成 API 凭证去访问其它接口。
/// </summary>
public sealed class HmacExportSigner(IOptions<JwtOptions> options) : IExportSigner
{
    private byte[] Key()
    {
        var s = options.Value.Secret;
        if (string.IsNullOrWhiteSpace(s))
            throw new InvalidOperationException("导出链接签名缺少密钥（JWT__SECRET 未配置）");
        return Encoding.UTF8.GetBytes(s);
    }

    public string Sign(string openid, long expireAt)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { sub = openid, exp = expireAt });
        var body = B64Url.Encode(payload);

        using var h = new HMACSHA256(Key());
        var sig = B64Url.Encode(h.ComputeHash(Encoding.ASCII.GetBytes(body)));
        return body + "." + sig;
    }

    public bool TryVerify(string? token, out string openid, out long expireAt)
    {
        openid = "";
        expireAt = 0;
        if (string.IsNullOrEmpty(token)) return false;

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return false;

        var body = token[..dot];
        var sig = token[(dot + 1)..];

        // 先验签再解析 payload：签名不过就不该有任何字节进入反序列化
        using var h = new HMACSHA256(Key());
        var expect = B64Url.Encode(h.ComputeHash(Encoding.ASCII.GetBytes(body)));
        // 定长比较，避免用字符串比较引入时序侧信道
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expect), Encoding.ASCII.GetBytes(sig)))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(B64Url.Decode(body));
            if (!doc.RootElement.TryGetProperty("sub", out var subEl)) return false;
            if (!doc.RootElement.TryGetProperty("exp", out var expEl)) return false;

            var sub = subEl.GetString();
            if (string.IsNullOrWhiteSpace(sub)) return false;

            var exp = expEl.GetInt64();
            if (exp < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return false;

            openid = sub;
            expireAt = exp;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static class B64Url
    {
        public static string Encode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Decode(string text)
        {
            var s = text.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: throw new FormatException("base64url 长度非法");
            }
            return Convert.FromBase64String(s);
        }
    }
}
