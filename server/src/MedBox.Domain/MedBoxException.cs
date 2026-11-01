namespace MedBox.Domain;

/// <summary>统一错误码（不得新增）。映射集中在 Api 层中间件。</summary>
public abstract class MedBoxException : Exception
{
    public int Code { get; }
    protected MedBoxException(int code, string message) : base(message) => Code = code;
}

public sealed class NotMemberException : MedBoxException
{
    public NotMemberException(string message = "你不是这个家庭的成员") : base(1002, message) { }
}

public sealed class ForbiddenException : MedBoxException
{
    public ForbiddenException(string message = "没有权限") : base(1003, message) { }
}

public sealed class QuotaExceededException : MedBoxException
{
    public QuotaExceededException(string message = "超出限额") : base(1004, message) { }
}

public sealed class ValidationException : MedBoxException
{
    public ValidationException(string message = "参数错误") : base(1005, message) { }
}
