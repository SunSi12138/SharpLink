#if SHARPLINK_STATIC_BINDING_PROTOTYPE
namespace SharpLink.Client;

internal sealed class StaticBindingCallSite(long contractId, RpcHash128 hash)
{
    internal long ContractId { get; } = contractId;
    internal RpcHash128 Hash { get; } = hash;
    internal StaticBoundTarget[] Snapshot = [];
}

// The token never leaves this tuple until the exact connection has been reserved.
internal readonly record struct StaticBoundTarget(ClientConnection Connection, ulong Token);

internal sealed partial class SharpLinkClient
{
    private readonly Lock _staticBindingGate = new();
    private readonly Dictionary<ClientProxyRegistration, StaticBindingCallSite> _staticBindingSites = [];

    private StaticBindingCallSite GetStaticBindingSite(ClientProxyRegistration registration)
    {
        if (Volatile.Read(ref _stopStarted) != 0)
            throw CreateConnectionClosedException("Client is stopping.");
        if (registration.Module is not null)
            throw StaticBindingWire.Error(StaticBindingFailure.DynamicUnsupported);
        var hash = FindOwningManifest(registration).RpcAssemblyHash;
        if (hash.IsEmpty)
            throw StaticBindingWire.Error(StaticBindingFailure.HashMismatch);
        lock (_staticBindingGate)
        {
            if (_staticBindingSites.TryGetValue(registration, out var site))
                return site;
            if (_staticBindingSites.Count >= StaticBindingWire.Capacity)
                throw StaticBindingWire.Error(StaticBindingFailure.Capacity);
            site = new StaticBindingCallSite(registration.Descriptor.ContractId, hash);
            _staticBindingSites.Add(registration, site);
            return site;
        }
    }

    // Deliberately internal until #805 review. Explicit preparation, not a hidden Get/network wait.
    // Call ConnectAsync first; this operation does not retry or wait for topology discovery.
    internal async Task PrepareStaticBindingAsync<T>(CancellationToken cancellationToken) where T : IService
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cluster is not null)
            throw new SharpLinkException(SharpLinkErrorCode.Unimplemented,
                "The static prototype supports a fixed endpoint and its connection pool, not clusters.");
        if (!Volatile.Read(ref _proxies).TryGetValue(typeof(T), out var registration))
            throw new InvalidOperationException("Local generated contract is not registered.");
        var site = GetStaticBindingSite(registration);
        var connections = Volatile.Read(ref _readyConnections);
        var manifests = Volatile.Read(ref _remoteContractManifestSnapshot);
        var work = new List<Task<StaticBoundTarget>>(connections.Length);
        foreach (var connection in connections)
        {
            var compatible = false;
            foreach (var manifest in manifests)
            {
                if (ReferenceEquals(manifest.Session, connection.Session) &&
                    manifest.Manifest.Contracts.TryGetValue(site.ContractId, out var hash) && hash == site.Hash)
                {
                    compatible = true;
                    break;
                }
            }
            if (compatible && connection.CanAcceptCalls)
                work.Add(PrepareTargetAsync(connection, site, cancellationToken));
        }
        if (work.Count == 0)
            throw StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute);
        var targets = await Task.WhenAll(work).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_poolGate)
        lock (_remoteContractManifestGate)
        {
            if (_shutdownCts.IsCancellationRequested)
                throw CreateConnectionClosedException("Client is stopping.");
            if (!ReferenceEquals(connections, Volatile.Read(ref _readyConnections)) ||
                !ReferenceEquals(manifests, Volatile.Read(ref _remoteContractManifestSnapshot)))
                throw StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute);
            lock (_staticBindingGate)
                Volatile.Write(ref site.Snapshot, targets);
        }
    }

    private static async Task<StaticBoundTarget> PrepareTargetAsync(
        ClientConnection connection, StaticBindingCallSite site, CancellationToken cancellationToken)
    {
        var ack = await connection.StaticBindings.BindAsync(site.ContractId, site.Hash)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return new StaticBoundTarget(connection, ack.Token);
    }

    private void PruneStaticBindingRoutes(ClientConnection[] connections)
    {
        lock (_staticBindingGate)
        {
            foreach (var site in _staticBindingSites.Values)
            {
                var current = Volatile.Read(ref site.Snapshot);
                if (current.Length != 0)
                    Volatile.Write(ref site.Snapshot, current.Where(target =>
                        Array.IndexOf(connections, target.Connection) >= 0 && target.Connection.CanAcceptCalls).ToArray());
            }
        }
    }

    private void InvalidateStaticBindingRoutes(RpcSession session)
    {
        lock (_staticBindingGate)
        {
            foreach (var site in _staticBindingSites.Values)
            {
                var current = Volatile.Read(ref site.Snapshot);
                if (current.Length != 0)
                    Volatile.Write(ref site.Snapshot,
                        current.Where(target => !ReferenceEquals(target.Connection.Session, session)).ToArray());
            }
        }
    }

    private StaticBoundTarget GetReadyStaticTarget(
        StaticBindingCallSite? site, RpcMethodDescriptor method, AttemptOutcomeState? outcome)
    {
        if (site is null || method.ContractId != site.ContractId)
            throw StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var targets = Volatile.Read(ref site.Snapshot);
            if (_shutdownCts.IsCancellationRequested || targets.Length == 0)
                break;
            if (outcome is not null && _fixedEndpoint is not null)
            {
                var candidate = new SharpLinkEndpointCandidate(
                    _fixedEndpoint, targets.Length, ActiveClientCallCount, generation: 0);
                if (!outcome.TryAcquire(candidate))
                    throw StaticBindingWire.Error(StaticBindingFailure.Unavailable);
            }
            // Same fixed-endpoint P2C policy as GetReadyConnection, not first-connection routing.
            var selected = targets[0];
            if (targets.Length > 1)
            {
                var first = Random.Shared.Next(targets.Length);
                var second = Random.Shared.Next(targets.Length - 1);
                if (second >= first) second++;
                selected = targets[first].Connection.ActiveCallCount <= targets[second].Connection.ActiveCallCount
                    ? targets[first] : targets[second];
            }
            if (selected.Connection.TryReserveCallAdmission(out var admitted))
            {
                if (ReferenceEquals(admitted, selected.Connection))
                {
                    if (admitted.ActiveCallCount != 0) EnsureExpansion();
                    return selected;
                }
                admitted.ReleaseCallAdmissionReservation(); // Never follow redirect with the old token.
            }
            outcome?.CompleteLocalFailure(StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute));
        }
        if (_shutdownCts.IsCancellationRequested)
            throw CreateConnectionClosedException("Client is stopping.");
        throw StaticBindingWire.Error(StaticBindingFailure.NoBoundRoute);
    }

    internal StaticBindingCallSite StaticBindingTestSite<T>() where T : IService
    {
        if (!Volatile.Read(ref _proxies).TryGetValue(typeof(T), out var registration))
            throw new InvalidOperationException("Local generated contract is not registered.");
        return GetStaticBindingSite(registration);
    }

    internal (int Routes, long Binds, int Pending) StaticBindingDiagnostics<T>() where T : IService
    {
        if (!Volatile.Read(ref _proxies).TryGetValue(typeof(T), out var registration)) return default;
        var targets = Volatile.Read(ref GetStaticBindingSite(registration).Snapshot);
        return (targets.Length, targets.Sum(static t => t.Connection.StaticBindings.Sent),
            targets.Sum(static t => t.Connection.StaticBindings.PendingCount));
    }
}
#endif
