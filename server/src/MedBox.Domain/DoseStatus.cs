namespace MedBox.Domain;

/// <summary>四态枚举。wire 值为 pending|taken|missed|skipped，late 不是状态。</summary>
public enum DoseStatus
{
    Pending = 0,
    Taken = 1,
    Missed = 2,
    Skipped = 3,
}

public static class DoseStatusWire
{
    public const string Pending = "pending";
    public const string Taken = "taken";
    public const string Missed = "missed";
    public const string Skipped = "skipped";

    public static string ToWire(this DoseStatus s) => s switch
    {
        DoseStatus.Taken => Taken,
        DoseStatus.Missed => Missed,
        DoseStatus.Skipped => Skipped,
        _ => Pending,
    };

    public static bool TryParse(string? wire, out DoseStatus status)
    {
        switch (wire)
        {
            case Taken: status = DoseStatus.Taken; return true;
            case Missed: status = DoseStatus.Missed; return true;
            case Skipped: status = DoseStatus.Skipped; return true;
            case Pending: status = DoseStatus.Pending; return true;
            default: status = DoseStatus.Pending; return false;
        }
    }
}
