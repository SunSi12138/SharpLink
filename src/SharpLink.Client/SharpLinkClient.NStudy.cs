namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private const int NStudyPrepareWaiterLimit = 64;
    private int _nStudyPrepareWaiters;

    internal int NStudyPrepareWaitersForDiagnostics => Volatile.Read(ref _nStudyPrepareWaiters);

    private void ReserveNStudyPrepareWaiter()
    {
        var observed = Volatile.Read(ref _nStudyPrepareWaiters);
        while (observed < NStudyPrepareWaiterLimit)
        {
            var current = Interlocked.CompareExchange(ref _nStudyPrepareWaiters, observed + 1, observed);
            if (current == observed)
                return;
            observed = current;
        }
        throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "The private Prepare waiter limit is reached.");
    }

    private void ReturnNStudyPrepareWaiter() => Interlocked.Decrement(ref _nStudyPrepareWaiters);

    internal async Task<NStudyInventorySnapshot> QueryNStudyRemoteInventoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = RequireSingleNStudyConnection();
        var snapshot = await connection.Session.QueryNStudyInventoryAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Status != NStudyInventoryStatus.Current)
            throw new SharpLinkException(snapshot.Status == NStudyInventoryStatus.CapacityExceeded
                ? SharpLinkErrorCode.ResourceExhausted : SharpLinkErrorCode.Unavailable,
                $"Private inventory outcome: {snapshot.Status}.");
        if (!connection.CanAcceptCalls || !ReferenceEquals(RequireSingleNStudyConnection(), connection))
            throw NStudyPreparedSite.Unavailable();
        return snapshot;
    }

    // Cold entry used only in the private N profile; one explicitly selected
    // Ready connection. A normal invocation never waits for or creates a Bind.
    internal async Task<NStudyAck> PrepareNStudyContractAsync(long contract, RpcHash128 hash,
        NStudyTarget expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await BindNStudyContractOnAsync(RequireSingleNStudyConnection(), contract, hash, expected, ct).ConfigureAwait(false);
    }

    private static async Task<NStudyAck> BindNStudyContractOnAsync(ClientConnection connection, long contract,
        RpcHash128 hash, NStudyTarget expected, CancellationToken ct)
    {
        var ack = await connection.Session.PrepareNStudyBindAsync(new NStudyBind(contract, hash, expected), ct).ConfigureAwait(false);
        if (ack.Status != NStudyBindStatus.Bound)
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, $"Private Bind outcome: {ack.Status}.");
        return ack;
    }

    internal Task<bool> ReleaseNStudyContractAsync(long contract, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return RequireSingleNStudyConnection().Session.ReleaseNStudyBindingAsync(contract, ct);
    }

    private ClientConnection RequireSingleNStudyConnection()
    {
        var ready = Volatile.Read(ref _readyConnections);
        if (ready.Length != 1)
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                "The private binding study requires exactly one ready connection.");
        return ready[0];
    }

    private bool TryGetNStudyClusterCandidate(ClientConnection connection, out SharpLinkEndpointCandidate candidate)
    {
        candidate = default;
        // Retirement closes each exact connection's gate before it is released from topology.
        return _cluster is { } cluster && cluster.TryGetEndpointCandidate(connection, out candidate) &&
            connection.CanAcceptCalls;
    }

    private ClientConnection[] CaptureNStudyReadyConnections() =>
        _cluster?.CaptureReadyConnections() ?? Volatile.Read(ref _readyConnections);

    private ClientConnection SelectNStudyPreparationConnection()
    {
        if (_cluster is { } cluster)
            return cluster.GetReadyConnection(method: null, retrySelection: null, attemptOutcome: null, reserveCall: false);

        foreach (var candidate in Volatile.Read(ref _readyConnections))
            if (candidate.CanAcceptCalls)
                return candidate;

        throw NStudyPreparedSite.Unavailable();
    }

    private bool IsNStudyPreparationConnectionCurrent(ClientConnection connection) =>
        _cluster is null
            ? connection.CanAcceptCalls && Array.IndexOf(Volatile.Read(ref _readyConnections), connection) >= 0
            : TryGetNStudyClusterCandidate(connection, out _);

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
