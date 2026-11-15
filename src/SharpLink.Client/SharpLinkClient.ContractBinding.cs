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

        if (manifest.ServerInstanceId != session.BindingServerInstanceId || manifest.RegistryEpoch != session.BindingRegistryEpoch)
            throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "Manifest does not belong to this handshake authority.");
        lock (_remoteContractManifestGate)
        {
            PruneDisconnectedRemoteContractManifestsLocked();
            if (_remoteContractManifests.TryGetValue(session, out var current))
            {
                if (manifest.Generation < current.Generation)
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

        ScheduleContractSiteRefresh();
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

    private ISharpLinkGeneratedAssemblyManifest FindOwningManifest(ClientProxyRegistration registration)
        => _assemblyRegistry.FindOwningManifest(registration);

    private readonly record struct RemoteContractManifestBinding(
        RpcSession Session,
        ProtocolV2ContractManifest Manifest);
}
