namespace MedBox.Domain;

/// <summary>
/// 周报推送的备注串（WEEKLY 模板 thing6，微信限 20 字）。
/// 模板没有独立的完成率/漏服次数字段，只能把它们折叠进这一行 —— 所以这种"压缩文案"必须可单测，
/// 否则长度超限只会在发送时以 47003 整批失败，界面上看不出来。
/// </summary>
public static class WeeklySummary
{
    public const int MaxLen = 20;

    public static string Build(int taken, int total, int missed)
    {
        var rate = total <= 0 ? 0 : (int)Math.Round(taken * 100.0 / total, MidpointRounding.AwayFromZero);
        var text = $"完成率{rate}% 漏服{missed}次";
        return text.Length <= MaxLen ? text : text.Substring(0, MaxLen);
    }
}
