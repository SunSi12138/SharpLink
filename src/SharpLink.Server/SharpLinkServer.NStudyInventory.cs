namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private NStudyAuthority ReadNStudySessionAuthority(ServerConnectionState connection)
        => new(_desiredSessionServerInstanceId, _nStudyRegistryEpoch, connection.NStudyPhysicalIdentity);

    internal SharpLinkVersionInventory ReadLocalVersionInventory()
        => CaptureNStudyServerInventory(default, int.MaxValue, peer: null, authenticated: false)
            .ToVersionInventory(remote: false);

    internal IReadOnlyList<SharpLinkVersionSession> ReadVersionPeers()
    {
        var peers = new List<SharpLinkVersionSession>();
        foreach (var (_, connection) in _connectionRegistry.EnumerateActiveEntries())
            if (IsVersionPeerReady(connection))
                peers.Add(NStudyInventorySnapshot.ToVersionSession(ReadNStudySessionAuthority(connection)));
        return peers.AsReadOnly();
    }

    internal async ValueTask<SharpLinkVersionInventory> QueryVersionPeerAsync(
        SharpLinkVersionSession peer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (_, connection) in _connectionRegistry.EnumerateActiveEntries())
        {
            if (peer != NStudyInventorySnapshot.ToVersionSession(ReadNStudySessionAuthority(connection)) ||
                !IsVersionPeerReady(connection))
                continue;

            var snapshot = await QueryNStudyClientInventoryAsync(connection, cancellationToken).ConfigureAwait(false);
            if (IsVersionPeerReady(connection))
                return snapshot.ToVersionInventory(remote: true);
            break;
        }
        throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The selected version peer is no longer Ready.");
    }

    private bool IsVersionPeerReady(ServerConnectionState connection)
        => CurrentState == ServerState.Running && connection.Session.CanAcceptCalls &&
            connection.LifecycleState == ServerConnectionLifecycleState.Ready &&
            connection.AuthenticationContext is { } auth && !auth.IsExpired() &&
            _connectionRegistry.TryGetValue(connection.Session.Id, out var current) && ReferenceEquals(current, connection);

    internal Task<NStudyInventorySnapshot> QueryNStudyClientInventoryAsync(ServerConnectionState connection, CancellationToken ct)
    {
        if (!_connectionRegistry.TryGetValue(connection.Session.Id, out var current) || !ReferenceEquals(current, connection) ||
            connection.LifecycleState != ServerConnectionLifecycleState.Ready ||
            connection.AuthenticationContext is not { } auth || auth.IsExpired())
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "No authenticated Ready peer for the private inventory query.");
        return connection.Session.QueryNStudyClientInventoryAsync(ReadNStudySessionAuthority(connection), ct);
    }

    private async ValueTask HandleNStudyInventoryAsync(ServerConnectionState connection, ulong operation, CancellationToken ct)
    {
        var session = connection.Session;
        var authority = ReadNStudySessionAuthority(connection);
        var authenticated = connection.AuthenticationContext is { } auth && !auth.IsExpired();
        var snapshot = CaptureNStudyServerInventory(authority,
            NStudyWire.GetInventoryCapacity(session.NegotiatedMaxFramePayloadBytes),
            connection, authenticated);
        using var timer = new CancellationTokenSource(RpcSession.NStudyTimeout, _runtimeContext.TimeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        await session.SendNStudyInventoryReplyAsync(operation, snapshot, budget.Token).ConfigureAwait(false);
    }

    private NStudyInventorySnapshot CaptureNStudyServerInventory(
        NStudyAuthority authority, int capacity, ServerConnectionState? peer, bool authenticated)
    {
        var status = NStudyInventoryStatus.Unavailable;
        var revision = 0L;
        NStudyInventoryEntry[] entries = [];
        lock (_registryGate)
        {
            if (peer is null
                ? CurrentState is ServerState.Created or ServerState.Starting or ServerState.Running
                : authenticated && CurrentState == ServerState.Running &&
                    peer.LifecycleState == ServerConnectionLifecycleState.Ready)
            {
                revision = _registryGeneration;
                var services = Volatile.Read(ref _services);
                if (services.Count > capacity)
                {
                    status = NStudyInventoryStatus.CapacityExceeded;
                }
                else
                {
                    status = NStudyInventoryStatus.Current;
                    entries = new NStudyInventoryEntry[services.Count];
                    var written = 0;
                    foreach (var (id, service) in services)
                    {
                        if (service.AcceptsCalls)
                            entries[written++] = new(id, service.NStudyContractHash, service.NStudyTarget);
                    }
                    if (written != entries.Length)
                        Array.Resize(ref entries, written);
                }
            }
        }
        Array.Sort(entries, static (left, right) => left.Contract.CompareTo(right.Contract));
        return new(authority, _nStudyRegistryEpoch, revision, status, entries);
    }
}
