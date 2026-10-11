namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public ContractReadinessSnapshot GetContractReadiness(ContractRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (Volatile.Read(ref _stopStarted) != 0 || requirement.LocalRetirement is { IsCompleted: true } ||
            State is SharpLinkConnectionState.Draining or SharpLinkConnectionState.Stopped or SharpLinkConnectionState.Faulted)
            return new ContractReadinessSnapshot(requirement, ContractObservationState.TargetStopped, 0, []);

        // Capture only the current routing topology. Retained manifest entries can belong to
        // removed endpoints, retired resolver generations, or old refreshing sessions.
        var sources = CaptureContractObservationSources();
        var bindings = Volatile.Read(ref _remoteContractManifestSnapshot);
        var observations = new ContractEndpointObservation[sources.Count];
        var compatibleEndpoints = new HashSet<string>(StringComparer.Ordinal);
        var aggregate = ContractObservationState.EndpointUnavailable;
        for (var index = 0; index < sources.Count; index++)
        {
            var observation = ObserveContract(sources[index], bindings, requirement);
            observations[index] = observation;
            if (observation.State == ContractObservationState.ObservedCompatible)
                compatibleEndpoints.Add(observation.EndpointId);
            if (ObservationPriority(observation.State) > ObservationPriority(aggregate))
                aggregate = observation.State;
        }
        return new ContractReadinessSnapshot(requirement, aggregate, compatibleEndpoints.Count, observations);
    }

    private List<ContractObservationSource> CaptureContractObservationSources()
    {
        var sources = new List<ContractObservationSource>();
        switch (_cluster)
        {
            case StaticClusterRuntime staticCluster:
                staticCluster.CaptureContractObservationSources(sources);
                break;
            case DynamicClusterRuntime dynamicCluster:
                dynamicCluster.CaptureContractObservationSources(sources);
                break;
            default:
                lock (_poolGate)
                    AddContractObservationSources(sources, _fixedEndpoint?.Id ?? "fixed", 0, _readyConnections);
                break;
        }
        return sources;
    }

    private static void AddContractObservationSources(
        List<ContractObservationSource> sources,
        string endpointId,
        long endpointGeneration,
        ClientConnection[] connections)
    {
        var before = sources.Count;
        foreach (var connection in connections)
        {
            if (connection.CanAcceptCalls)
                sources.Add(new ContractObservationSource(endpointId, endpointGeneration, connection));
        }
        if (sources.Count == before)
            sources.Add(new ContractObservationSource(endpointId, endpointGeneration, null));
    }

    private static ContractEndpointObservation ObserveContract(
        ContractObservationSource source,
        RemoteContractManifestBinding[] bindings,
        ContractRequirement requirement)
    {
        var connection = source.Connection;
        if (connection is null || !connection.CanAcceptCalls)
            return new ContractEndpointObservation(source.EndpointId, connection?.Session.Id ?? string.Empty,
                source.EndpointGeneration, null, ContractObservationState.EndpointUnavailable, null);

        var session = connection.Session;
        ProtocolV2ContractManifest? manifest = null;
        foreach (var binding in bindings)
        {
            if (ReferenceEquals(binding.Session, session))
            {
                manifest = binding.Manifest;
                break;
            }
        }
        if (manifest is null)
            return new ContractEndpointObservation(source.EndpointId, session.Id, source.EndpointGeneration,
                null, ContractObservationState.ManifestNotObserved, null);

        var registrationId = manifest.RegistrationIds.GetValueOrDefault(requirement.ContractId);
        var present = manifest.Contracts.TryGetValue(requirement.ContractId, out var hash);
        var state = !present
            ? ContractObservationState.Absent
            : hash == requirement.RpcAssemblyHash &&
              (session.NegotiatedCapabilities & ProtocolV2Capabilities.ContractAdmission) != 0 &&
              requirement.Matches(manifest.ServerInstanceId, manifest.RegistryEpoch, manifest.Generation, registrationId)
                ? ContractObservationState.ObservedCompatible
                : ContractObservationState.ObservedIncompatible;
        return new ContractEndpointObservation(source.EndpointId, session.Id, source.EndpointGeneration,
            manifest.Generation, state, present ? hash : null,
            manifest.ServerInstanceId, manifest.RegistryEpoch, registrationId);
    }

    private static int ObservationPriority(ContractObservationState state)
        => state switch
        {
            ContractObservationState.ObservedCompatible => 4,
            ContractObservationState.ObservedIncompatible => 3,
            ContractObservationState.Absent => 2,
            ContractObservationState.ManifestNotObserved => 1,
            _ => 0
        };

    private readonly record struct ContractObservationSource(
        string EndpointId,
        long EndpointGeneration,
        ClientConnection? Connection);

    private sealed partial class StaticClusterRuntime
    {
        public void CaptureContractObservationSources(List<ContractObservationSource> sources)
        {
            lock (_gate)
            {
                foreach (var endpoint in _endpoints)
                    AddContractObservationSources(sources, endpoint.Configuration.Endpoint.Id, 0, endpoint.ReadyConnections);
            }
        }
    }

    private sealed partial class DynamicClusterRuntime
    {
        public void CaptureContractObservationSources(List<ContractObservationSource> sources)
        {
            lock (_gate)
            {
                foreach (var endpoint in _current.Current)
                {
                    if (!endpoint.Retiring)
                        AddContractObservationSources(sources, endpoint.Configuration.Endpoint.Id,
                            endpoint.Generation, endpoint.ReadyConnections);
                }
            }
        }
    }
}
