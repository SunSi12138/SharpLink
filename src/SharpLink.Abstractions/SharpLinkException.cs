namespace SharpLink.Abstractions;

public class SharpLinkException : Exception
{
    public SharpLinkErrorCode Code { get; }

    public SharpLinkException(SharpLinkErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public SharpLinkException(SharpLinkErrorCode code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

}
