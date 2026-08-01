namespace SharpLink.Abstractions;

/// <summary>Describes the result returned by a server authentication provider.</summary>
/// <param name="IsAuthenticated">Whether authentication succeeded.</param>
/// <param name="ErrorCode">The rejection code when authentication failed.</param>
/// <param name="ErrorMessage">The safe rejection message returned to the peer.</param>
/// <param name="Context">The established or partially established identity context.</param>
public readonly record struct SharpLinkAuthenticationResult(
    bool IsAuthenticated,
    SharpLinkErrorCode ErrorCode,
    string? ErrorMessage,
    SharpLinkAuthenticationContext? Context)
{
    /// <summary>Gets a successful result without an identity context.</summary>
    public static SharpLinkAuthenticationResult Success => new(true, SharpLinkErrorCode.Unknown, null, null);

    /// <summary>Creates a successful result with an established identity context.</summary>
    /// <param name="context">The non-null authenticated identity.</param>
    /// <returns>A successful authentication result.</returns>
    public static SharpLinkAuthenticationResult Authenticate(SharpLinkAuthenticationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SharpLinkAuthenticationResult(true, SharpLinkErrorCode.Unknown, null, context);
    }

    /// <summary>Creates a rejected authentication result.</summary>
    /// <param name="errorCode">A concrete error code returned to the peer.</param>
    /// <param name="errorMessage">An optional safe peer-facing message.</param>
    /// <param name="context">An optional partial identity context for diagnostics or policy.</param>
    /// <returns>A failed authentication result.</returns>
    public static SharpLinkAuthenticationResult Reject(
        SharpLinkErrorCode errorCode = SharpLinkErrorCode.AuthenticationRejected,
        string? errorMessage = null,
        SharpLinkAuthenticationContext? context = null)
    {
        if (errorCode == SharpLinkErrorCode.Unknown)
            throw new ArgumentException("Authentication rejection must use a concrete error code.", nameof(errorCode));
        if (!Enum.IsDefined(errorCode))
            throw new ArgumentOutOfRangeException(nameof(errorCode));

        return new SharpLinkAuthenticationResult(
            IsAuthenticated: false,
            ErrorCode: errorCode,
            ErrorMessage: string.IsNullOrWhiteSpace(errorMessage)
                ? GetDefaultMessage(errorCode)
                : errorMessage,
            Context: context);
    }

    private static string GetDefaultMessage(SharpLinkErrorCode errorCode) => errorCode switch
    {
        SharpLinkErrorCode.AuthenticationExpired => "Authentication expired.",
        SharpLinkErrorCode.AuthorizationDenied => "Authorization denied.",
        SharpLinkErrorCode.ProtocolViolation => "Protocol violation.",
        _ => "Authentication rejected."
    };
}
