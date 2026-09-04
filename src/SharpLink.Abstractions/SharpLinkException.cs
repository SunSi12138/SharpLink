namespace SharpLink.Abstractions;

/// <summary>Represents a failure that has a stable SharpLink wire error code.</summary>
public class SharpLinkException : Exception
{
    /// <summary>Gets the concrete error code sent to or received from the peer.</summary>
    public SharpLinkErrorCode Code { get; }

    /// <summary>
    /// Gets the stable machine-readable detail code scoped by <see cref="Code"/>.
    /// </summary>
    /// <remarks>
    /// A value of <see cref="SharpLinkErrorDetails.Unspecified"/> means that no finer-grained
    /// classification was supplied. Unknown non-zero values are preserved so newer peers can add
    /// details without older callers ambiguously reinterpreting them.
    /// </remarks>
    public ushort DetailCode { get; }

    /// <summary>Creates a SharpLink failure with a message.</summary>
    /// <param name="code">A concrete non-unknown wire error code.</param>
    /// <param name="message">The diagnostic error message.</param>
    public SharpLinkException(SharpLinkErrorCode code, string message)
        : this(code, SharpLinkErrorDetails.Unspecified, message)
    {
    }

    /// <summary>Creates a SharpLink failure with a stable detail code and message.</summary>
    /// <param name="code">A concrete non-unknown wire error code.</param>
    /// <param name="detailCode">The machine-readable detail code scoped by <paramref name="code"/>.</param>
    /// <param name="message">The diagnostic error message.</param>
    public SharpLinkException(SharpLinkErrorCode code, ushort detailCode, string message)
        : base(message)
    {
        Code = ValidateCode(code);
        DetailCode = detailCode;
    }

    /// <summary>Creates a SharpLink failure with a message and underlying cause.</summary>
    /// <param name="code">A concrete non-unknown wire error code.</param>
    /// <param name="message">The diagnostic error message.</param>
    /// <param name="innerException">The underlying cause, when present.</param>
    public SharpLinkException(SharpLinkErrorCode code, string message, Exception? innerException)
        : this(code, SharpLinkErrorDetails.Unspecified, message, innerException)
    {
    }

    /// <summary>Creates a SharpLink failure with a stable detail code, message, and underlying cause.</summary>
    /// <param name="code">A concrete non-unknown wire error code.</param>
    /// <param name="detailCode">The machine-readable detail code scoped by <paramref name="code"/>.</param>
    /// <param name="message">The diagnostic error message.</param>
    /// <param name="innerException">The underlying cause, when present.</param>
    public SharpLinkException(
        SharpLinkErrorCode code,
        ushort detailCode,
        string message,
        Exception? innerException)
        : base(message, innerException)
    {
        Code = ValidateCode(code);
        DetailCode = detailCode;
    }

    private static SharpLinkErrorCode ValidateCode(SharpLinkErrorCode code)
    {
        if (code == SharpLinkErrorCode.Unknown || !Enum.IsDefined(code))
            throw new ArgumentOutOfRangeException(nameof(code), code, "A concrete wire error code is required.");
        return code;
    }
}
