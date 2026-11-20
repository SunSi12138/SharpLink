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

    private static void RequireNStudyShape(in RpcMethodDescriptor method)
    {
        if (method.HasClientStreams || method.Kind is not (RpcMethodKind.Unary or RpcMethodKind.OneWay))
            throw new SharpLinkException(SharpLinkErrorCode.Unimplemented,
                "This private checkpoint accepts only bound Unary and plain OneWay.");
    }
}
