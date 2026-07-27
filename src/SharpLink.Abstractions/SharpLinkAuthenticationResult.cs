namespace SharpLink.Abstractions;

public readonly record struct SharpLinkAuthenticationResult(
    bool IsAuthenticated,
    SharpLinkErrorCode ErrorCode,
    string? ErrorMessage,
    SharpLinkAuthenticationContext? Context)
{
    public static SharpLinkAuthenticationResult Success => new(true, SharpLinkErrorCode.Unknown, null, null);

    public static SharpLinkAuthenticationResult Authenticate(SharpLinkAuthenticationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SharpLinkAuthenticationResult(true, SharpLinkErrorCode.Unknown, null, context);
    }

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
