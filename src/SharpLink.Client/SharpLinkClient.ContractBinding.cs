namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private readonly ClientContractRouteTable _contractRoutes = new();
    private readonly Lock _remoteContractManifestGate = new();
    private readonly Dictionary<RpcSession, ProtocolV2ContractManifest> _remoteContractManifests = [];
    private RemoteContractManifestBinding[] _remoteContractManifestSnapshot = [];

    private void ClearRemoteContractRoutes()
    {
        lock (_remoteContractManifestGate)
        {
            _remoteContractManifests.Clear();
            Volatile.Write(ref _remoteContractManifestSnapshot, []);
        }
        _contractRoutes.Clear();
    }

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
        _contractRoutes.PublishManifest(session, manifest);
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

    private ISharpLinkGeneratedAssemblyManifest FindOwningManifest(ClientProxyRegistration registration)
        => _assemblyRegistry.FindOwningManifest(registration);

    private readonly record struct RemoteContractManifestBinding(
        RpcSession Session,
        ProtocolV2ContractManifest Manifest);
}
