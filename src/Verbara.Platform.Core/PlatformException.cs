namespace Verbara.Platform.Core;

public class PlatformException : Exception
{
    public string Code { get; }

    public PlatformException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public PlatformException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }
}
