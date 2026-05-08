namespace SharpLink.Abstractions;

public class SharpLinkException : Exception
{
    private const string PayloadPrefix = "SLERR|";
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

    public string ToPayloadMessage() => $"{PayloadPrefix}{(int)Code}|{Message}";

    public static bool TryParsePayloadMessage(string? payloadMessage, out SharpLinkException? exception)
    {
        exception = null;
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

        var message = payloadMessage[(separatorIndex + 1)..];
        exception = new SharpLinkException((SharpLinkErrorCode)codeValue, message);
        return true;
    }
}
