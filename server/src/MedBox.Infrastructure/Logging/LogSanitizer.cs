using System.Text.RegularExpressions;

namespace MedBox.Infrastructure.Logging;

/// <summary>
/// 落库前脱敏。控制台日志给人看、泄露面小；进库的日志会被长期保存、被各种人查，
/// 所以凭据类内容必须在写入前打码，不能指望"以后想起来再清"。
/// 只处理高置信度的模式（token / 密码 / 手机号），不做过度猜测——误删内容会让日志失去排查价值。
/// </summary>
public static class LogSanitizer
{
    // Authorization: Bearer eyJhbGciOi... / token=xxx / access_token":"xxx"
    private static readonly Regex BearerRegex = new(
        @"(Bearer\s+)[A-Za-z0-9\-._~+/]{8,}=*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SecretRegex = new(
        @"(?<key>password|passwd|pwd|secret|token|access_token|refresh_token|jwt)(?<sep>\s*[:=]\s*""?)(?<val>[^""\s,;&}]{4,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PhoneRegex = new(
        @"(?<!\d)(1[3-9]\d)\d{4}(\d{4})(?!\d)", RegexOptions.Compiled);

    public static string? Sanitize(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = text;
        if (s.Length > maxChars) s = s[..maxChars] + "…(截断)";

        s = BearerRegex.Replace(s, "$1***");
        s = SecretRegex.Replace(s, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}***");
        s = PhoneRegex.Replace(s, "$1****$2");
        return s;
    }
}
