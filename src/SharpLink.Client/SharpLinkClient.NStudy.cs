namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    // Cold entry used only in the private N profile; one explicitly selected
    // Ready connection. A normal invocation never waits for or creates a Bind.
    internal async Task<NStudyAck> PrepareNStudyContractAsync(long contract, RpcHash128 hash,
        NStudyTarget expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ready = Volatile.Read(ref _readyConnections);
        if (ready.Length != 1)
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                "The private binding study requires exactly one ready connection.");
        var session = ready[0].Session;
        var ack = await session.PrepareNStudyBindAsync(new NStudyBind(contract, hash, expected), ct).ConfigureAwait(false);
        if (ack.Status != NStudyBindStatus.Bound)
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, $"Private Bind outcome: {ack.Status}.");
        return ack;
    }

    internal Task<bool> ReleaseNStudyContractAsync(long contract, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ready = Volatile.Read(ref _readyConnections);
        if (ready.Length != 1)
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                "The private binding study requires exactly one ready connection.");
        return ready[0].Session.ReleaseNStudyBindingAsync(contract, ct);
    }

    private static void RequireNStudyShape(in RpcMethodDescriptor method)
    {
        var supported = method.Kind switch
        {
            RpcMethodKind.Unary or RpcMethodKind.ServerStreaming => !method.HasClientStreams,
            RpcMethodKind.OneWay => method.HasClientStreams
                ? method.ClientStreamCount is > 0 and <= NStudyWire.OneWayInputLimit
                : method.ClientStreamCount == 0,
            RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming =>
                method.HasClientStreams && method.ClientStreamCount > 0,
            _ => false
        };
        if (!supported)
            throw new SharpLinkException(SharpLinkErrorCode.Unimplemented,
                "This private checkpoint does not support that bound RPC shape.");
    }
}
