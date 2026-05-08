namespace SharpLink.Abstractions;

public readonly record struct SharpLinkAuthenticationResult(
    bool IsAuthenticated,
    SharpLinkErrorCode ErrorCode,
    string? ErrorMessage,
    SharpLinkAuthenticationContext? Context)
{
    private const string PayloadPrefix = "SLAUTH|";

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

        return new SharpLinkAuthenticationResult(
            IsAuthenticated: false,
            ErrorCode: errorCode,
            ErrorMessage: string.IsNullOrWhiteSpace(errorMessage)
                ? GetDefaultMessage(errorCode)
                : errorMessage,
            Context: context);
    }

    public string ToPayloadMessage()
    {
        if (IsAuthenticated)
            throw new InvalidOperationException("Successful authentication results do not produce an error payload.");

        return $"{PayloadPrefix}{(int)ErrorCode}|{ErrorMessage ?? string.Empty}";
    }

    public static bool TryParsePayloadMessage(string? payloadMessage, out SharpLinkAuthenticationResult result)
    {
        result = default;
        if (string.IsNullOrEmpty(payloadMessage) || !payloadMessage.StartsWith(PayloadPrefix, StringComparison.Ordinal))
            return false;

        var separatorIndex = payloadMessage.IndexOf('|', PayloadPrefix.Length);
        if (separatorIndex < 0)
            return false;

        var codeText = payloadMessage[PayloadPrefix.Length..separatorIndex];
        if (!int.TryParse(codeText, out var codeValue) ||
            !Enum.IsDefined(typeof(SharpLinkErrorCode), codeValue))
        {
            return false;
        }

        var errorCode = (SharpLinkErrorCode)codeValue;
        var errorMessage = payloadMessage[(separatorIndex + 1)..];
        result = Reject(errorCode, errorMessage);
        return true;
    }

    private static string GetDefaultMessage(SharpLinkErrorCode errorCode) => errorCode switch
    {
        SharpLinkErrorCode.AuthenticationExpired => "Authentication expired.",
        SharpLinkErrorCode.AuthorizationDenied => "Authorization denied.",
        SharpLinkErrorCode.ProtocolViolation => "Protocol violation.",
        _ => "Authentication rejected."
    };
}
