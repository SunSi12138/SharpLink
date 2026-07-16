namespace SharpLink.Server;

internal sealed class DefaultRpcExceptionMapper(bool includeDetails) : IRpcExceptionMapper
{
    public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);

        if (exception is SharpLinkException sharpLinkException)
            return sharpLinkException;
        if (exception is OperationCanceledException)
        {
            return new SharpLinkException(
                SharpLinkErrorCode.Cancelled,
                "The server call was cancelled.",
                exception);
        }

        return new SharpLinkException(
            SharpLinkErrorCode.Internal,
            includeDetails && !string.IsNullOrWhiteSpace(exception.Message)
                ? exception.Message
                : "Internal service error.",
            exception);
    }
}
