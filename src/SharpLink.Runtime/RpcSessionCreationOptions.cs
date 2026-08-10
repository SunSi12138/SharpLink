namespace SharpLink.Runtime;

internal enum RpcSessionRole : byte
{
    Client,
    Server
}

internal delegate SharpLinkException RpcSessionServiceExceptionMapper(
    RpcSession session,
    long requestId,
    long contractId,
    long methodId,
    Exception exception);

/// <summary>Immutable construction snapshot for one fully configured RPC session.</summary>
internal sealed class RpcSessionCreationOptions
{
    internal RpcSessionCreationOptions(
        RpcSessionRole role,
        SharpLinkRuntimeContext runtimeContext,
        RpcSessionFlushOptions? flushOptions = null,
        RpcSessionServiceExceptionMapper? serviceExceptionMapper = null)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentNullException.ThrowIfNull(runtimeContext);
        if (flushOptions is { } configuredFlushOptions)
        {
            RpcSessionFlushOptions.Validate(
                configuredFlushOptions.FlushSizeThreshold,
                configuredFlushOptions.MaxLatency);
        }

        Role = role;
        RuntimeContext = runtimeContext;
        FlushOptions = flushOptions;
        ServiceExceptionMapper = serviceExceptionMapper;
    }

    internal RpcSessionRole Role { get; }

    internal SharpLinkRuntimeContext RuntimeContext { get; }

    internal RpcSessionFlushOptions? FlushOptions { get; }

    internal RpcSessionServiceExceptionMapper? ServiceExceptionMapper { get; }

    internal string TelemetrySide => Role == RpcSessionRole.Client ? "client" : "server";
}
