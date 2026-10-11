namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private readonly Lock _remoteContractManifestGate = new();
    private readonly Dictionary<RpcSession, ProtocolV2ContractManifest> _remoteContractManifests = [];
    private RemoteContractManifestBinding[] _remoteContractManifestSnapshot = [];

    private void PublishRemoteContractManifest(
        RpcSession session,
        ProtocolV2ContractManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(manifest);

        lock (_remoteContractManifestGate)
        {
            PruneDisconnectedRemoteContractManifestsLocked();
            if (_remoteContractManifests.TryGetValue(session, out var current))
            {
                if (manifest.ServerInstanceId == current.ServerInstanceId &&
                    manifest.RegistryEpoch == current.RegistryEpoch && manifest.Generation < current.Generation)
                    return;
                _remoteContractManifests[session] = manifest;
            }
            else
            {
                _remoteContractManifests.Add(session, manifest);
            }

            var snapshot = new RemoteContractManifestBinding[_remoteContractManifests.Count];
            var index = 0;
            foreach (var pair in _remoteContractManifests)
                snapshot[index++] = new RemoteContractManifestBinding(pair.Key, pair.Value);
            Volatile.Write(ref _remoteContractManifestSnapshot, snapshot);
        }
        PulseRemoteContractWaiters();

    }

    private void PruneDisconnectedRemoteContractManifestsLocked()
    {
        if (_remoteContractManifests.Count == 0)
            return;

        List<RpcSession>? disconnected = null;
        foreach (var pair in _remoteContractManifests)
        {
            if (!pair.Key.IsConnected)
                (disconnected ??= []).Add(pair.Key);
        }

        if (disconnected is null)
            return;
        for (var index = 0; index < disconnected.Count; index++)
            _remoteContractManifests.Remove(disconnected[index]);
    }

    private void ValidateLocalContractLifetime(ClientProxyRegistration registration)
    {
        if (Volatile.Read(ref _stopStarted) != 0 ||
            State is SharpLinkConnectionState.Stopped or SharpLinkConnectionState.Faulted)
            throw CreateConnectionClosedException("Client is not accepting new proxies.");
        if (registration.Module is { State: not SharpLinkDynamicModuleState.Running })
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The local RPC module is retiring.");
    }

    private ContractRequirement GetLocalContractRequirement(ClientProxyRegistration registration)
    {
        ValidateLocalContractLifetime(registration);
        var manifest = _assemblyRegistry.FindOwningManifest(registration);
        return new ContractRequirement(registration.Descriptor.ContractId, manifest.RpcAssemblyHash)
        { LocalRetirement = registration.Module?.Retirement };
    }

    private IRpcChannel CreateBoundContractChannel(ClientProxyRegistration registration, ContractRequirement requirement)
        => registration.Module is null
            ? new SharpLinkContractRpcChannel(this, requirement)
            : new SharpLinkModuleRpcChannel(this, registration.Module, requirement);

    private bool IsContractCompatible(RpcSession session, ContractRequirement requirement)
    {
        if (!session.IsConnected || session.ProtocolPhase != RpcSessionProtocolPhase.Ready ||
            (session.NegotiatedCapabilities & ProtocolV2Capabilities.ContractAdmission) == 0)
            return false;
        foreach (var binding in Volatile.Read(ref _remoteContractManifestSnapshot))
        {
            if (ReferenceEquals(binding.Session, session))
                return binding.Manifest.Contracts.TryGetValue(requirement.ContractId, out var hash) &&
                    hash == requirement.RpcAssemblyHash &&
                    requirement.Matches(binding.Manifest.ServerInstanceId, binding.Manifest.RegistryEpoch,
                        binding.Manifest.Generation, binding.Manifest.RegistrationIds.GetValueOrDefault(requirement.ContractId));
        }
        return false;
    }

    private ClientConnection? SelectContractConnection(
        ClientConnection[] connections, ContractRequirement? requirement)
    {
        if (requirement is null)
            return EndpointSelectionKernel.SelectConnection(connections);
        // Reservoir sampling keeps the established power-of-two least-loaded choice without
        // allocating a filtered array on the no-options path.
        ClientConnection? first = null;
        ClientConnection? second = null;
        var seen = 0;
        foreach (var candidate in connections)
        {
            if (!IsContractCompatible(candidate.Session, requirement))
                continue;
            seen++;
            if (seen == 1) first = candidate;
            else if (seen == 2) second = candidate;
            else
            {
                var slot = Random.Shared.Next(seen);
                if (slot == 0) first = candidate;
                else if (slot == 1) second = candidate;
            }
        }
        var selected = second is not null && second.ActiveCallCount < first!.ActiveCallCount ? second : first;
        if (selected is not null && TryReserveCompatible(selected, requirement, out var admitted))
            return admitted;
        foreach (var candidate in connections)
            if (!ReferenceEquals(candidate, selected) && IsContractCompatible(candidate.Session, requirement) &&
                TryReserveCompatible(candidate, requirement, out admitted))
                return admitted;
        return null;
    }

    private bool TryReserveCompatible(ClientConnection candidate, ContractRequirement requirement,
        out ClientConnection admitted)
    {
        if (!candidate.TryReserveCallAdmission(out admitted))
            return false;
        // Reservation may redirect across a blue/green replacement. Send uses this exact binding.
        if (IsContractCompatible(admitted.Session, requirement))
            return true;
        admitted.ReleaseCallAdmissionReservation();
        return false;
    }

    private ulong ApplyContractExclusions<TEndpoint>(TEndpoint[] endpoints, ulong excluded,
        ContractRequirement? requirement, Func<TEndpoint, ClientConnection[]> connections)
    {
        if (requirement is null)
            return excluded;
        ulong incompatible = 0;
        for (var index = 0; index < endpoints.Length; index++)
        {
            var compatible = false;
            foreach (var connection in connections(endpoints[index]))
                if (IsContractCompatible(connection.Session, requirement))
                {
                    compatible = true;
                    break;
                }
            if (!compatible) incompatible |= 1UL << index;
        }
        var all = endpoints.Length == 64 ? ulong.MaxValue : (1UL << endpoints.Length) - 1;
        // A retry may revisit the only compatible endpoint once its eligible cycle is exhausted.
        return ((excluded | incompatible) & all) == all ? incompatible : excluded | incompatible;
    }

    private readonly record struct RemoteContractManifestBinding(
        RpcSession Session,
        ProtocolV2ContractManifest Manifest);
}
