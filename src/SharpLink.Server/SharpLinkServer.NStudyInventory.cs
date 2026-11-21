namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private NStudyAuthority ReadNStudySessionAuthority(ServerConnectionState connection)
        => new(_desiredSessionServerInstanceId, _nStudyRegistryEpoch, connection.NStudyPhysicalIdentity);

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
        var status = NStudyInventoryStatus.Unavailable;
        var revision = 0L;
        NStudyInventoryEntry[] entries = [];
        lock (_registryGate)
        {
            if (authenticated && CurrentState == ServerState.Running &&
                connection.LifecycleState == ServerConnectionLifecycleState.Ready)
            {
                revision = _registryGeneration;
                var services = Volatile.Read(ref _services);
                if (services.Count > NStudyWire.GetInventoryCapacity(session.NegotiatedMaxFramePayloadBytes))
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
        using var timer = new CancellationTokenSource(RpcSession.NStudyTimeout, _runtimeContext.TimeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        await session.SendNStudyInventoryReplyAsync(operation,
                new(authority, authority.RegistryEpoch, revision, status, entries), budget.Token)
            .ConfigureAwait(false);
    }
}
