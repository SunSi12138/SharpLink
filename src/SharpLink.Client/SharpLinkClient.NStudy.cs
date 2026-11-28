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
        var snapshot = await QueryNStudyRemoteInventoryOnAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(RequireSingleNStudyConnection(), connection))
            throw NStudyPreparedSite.Unavailable();
        return snapshot;
    }

    internal async ValueTask<SharpLinkVersionInventory> QueryVersionConnectionAsync(
        SharpLinkVersionConnection selector, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = RequireVersionConnection(selector);
        var snapshot = await QueryNStudyRemoteInventoryOnAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!IsNStudyPreparationConnectionCurrent(connection))
            throw NStudyPreparedSite.Unavailable();
        return snapshot.ToVersionInventory(remote: true);
    }

    private static async Task<NStudyInventorySnapshot> QueryNStudyRemoteInventoryOnAsync(
        ClientConnection connection, CancellationToken cancellationToken)
    {
        var snapshot = await connection.Session.QueryNStudyInventoryAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Status != NStudyInventoryStatus.Current)
            throw new SharpLinkException(snapshot.Status == NStudyInventoryStatus.CapacityExceeded
                ? SharpLinkErrorCode.ResourceExhausted : SharpLinkErrorCode.Unavailable,
                $"Private inventory outcome: {snapshot.Status}.");
        return snapshot;
    }

    internal IReadOnlyList<SharpLinkVersionConnection> ReadVersionConnections()
    {
        var ready = CaptureVersionConnections();
        var selectors = new List<SharpLinkVersionConnection>(ready.Length);
        foreach (var connection in ready)
        {
            if (!IsNStudyPreparationConnectionCurrent(connection))
                continue;
            selectors.Add(new(connection, connection.EndpointId ?? _fixedEndpoint?.Id,
                connection.Session.ReadObservedNStudyVersionSession()));
        }
        return selectors.AsReadOnly();
    }

    private ClientConnection RequireVersionConnection(SharpLinkVersionConnection selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (!selector.TryGetConnection(out var connection) || !IsNStudyPreparationConnectionCurrent(connection))
            throw NStudyPreparedSite.Unavailable();
        return connection;
    }

    private ClientConnection RequireVersionConnection(SharpLinkVersionSession peer)
    {
        var authority = new NStudyAuthority(peer.ServerInstanceId, peer.ServerRegistryEpoch, peer.PhysicalSessionId);
        ClientConnection? match = null;
        foreach (var connection in CaptureVersionConnections())
        {
            if (!connection.Session.HasNStudyAuthority(authority) || !IsNStudyPreparationConnectionCurrent(connection))
                continue;
            if (match is not null)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                    "The observed version session does not select a unique current connection.");
            match = connection;
        }
        return match ?? throw NStudyPreparedSite.Unavailable();
    }

    private ClientConnection[] CaptureVersionConnections()
    {
        var ready = CaptureNStudyReadyConnections();
        if (ready.Length > NStudyContractPreparation.MaximumCohortSize)
            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "Too many Ready connections for version coordination.");
        return ready;
    }

    private static SharpLinkException CreatePreparedBindingCapacityError()
        => SharpLinkResourceExhaustion.Create(SharpLinkResourceExhaustion.PreparedBindingCapacity,
            $"The connection's {NStudyWire.BindingLimit} prepared binding slots are occupied.");

    internal Task<bool> ReleaseNStudyContractAsync(long contract, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return RequireSingleNStudyConnection().Session.ReleaseNStudyBindingAsync(contract, ct);
    }

    private ClientConnection RequireSingleNStudyConnection()
    {
        var ready = CaptureNStudyReadyConnections();
        if (ready.Length != 1 || !IsNStudyPreparationConnectionCurrent(ready[0]))
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                "This operation requires exactly one ready connection.");
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
