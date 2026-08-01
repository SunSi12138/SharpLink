namespace SharpLink.Abstractions;

/// <summary>Represents a failure that has a stable SharpLink wire error code.</summary>
public class SharpLinkException : Exception
{
    /// <summary>Gets the concrete error code sent to or received from the peer.</summary>
    public SharpLinkErrorCode Code { get; }

    /// <summary>Creates a SharpLink failure with a message.</summary>
    /// <param name="code">A concrete non-unknown wire error code.</param>
    /// <param name="message">The diagnostic error message.</param>
    public SharpLinkException(SharpLinkErrorCode code, string message)
        : base(message)
    {
        Code = ValidateCode(code);
    }

    /// <summary>Creates a SharpLink failure with a message and underlying cause.</summary>
    /// <param name="code">A concrete non-unknown wire error code.</param>
    /// <param name="message">The diagnostic error message.</param>
    /// <param name="innerException">The underlying cause, when present.</param>
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
