namespace SharpLink.Abstractions;

public class SharpLinkException : Exception
{
    public SharpLinkErrorCode Code { get; }

    public SharpLinkException(SharpLinkErrorCode code, string message)
        : base(message)
    {
        Code = ValidateCode(code);
    }

    public SharpLinkException(SharpLinkErrorCode code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = ValidateCode(code);
    }

    private static SharpLinkErrorCode ValidateCode(SharpLinkErrorCode code)
    {
        if (code == SharpLinkErrorCode.Unknown || !Enum.IsDefined(code))
            throw new ArgumentOutOfRangeException(nameof(code), code, "A concrete wire error code is required.");
        return code;
    }
}
