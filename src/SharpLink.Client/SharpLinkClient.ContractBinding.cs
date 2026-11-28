using System.Reflection;

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
                if (manifest.Generation < current.Generation)
                    return;
                _remoteContractManifests[session] = manifest;
            }
            else
                _remoteContractManifests.Add(session, manifest);

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

    private ISharpLinkGeneratedAssemblyManifest FindOwningManifest(ClientProxyRegistration registration)
        => _assemblyRegistry.FindOwningManifest(registration);

    private void ValidateNStudyRemoteContractOn(ClientProxyRegistration registration, RpcSession session)
    {
        foreach (var binding in Volatile.Read(ref _remoteContractManifestSnapshot))
        {
            if (!ReferenceEquals(binding.Session, session))
                continue;
            ValidateRemoteContractAssembly(registration, FindOwningManifest(registration),
                registration.NStudyAssemblyHash, session, binding.Manifest);
            return;
        }
        throw NStudyPreparedSite.Unavailable();
    }

    private static void ValidateRemoteContractAssembly(
        ClientProxyRegistration registration,
        ISharpLinkGeneratedAssemblyManifest localManifest,
        RpcHash128 localHash,
        RpcSession session,
        ProtocolV2ContractManifest remoteManifest)
    {
        var contract = registration.Descriptor;
        if (!remoteManifest.Contracts.TryGetValue(contract.ContractId, out var remoteHash))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.FailedPrecondition,
                $"Remote contract manifest does not advertise RPC contract '{contract.ContractName}' " +
                $"({contract.ContractId}). Local assembly='{localManifest.OwnerAssembly.FullName}', " +
                $"local RpcAssemblyHash='{localHash}', session='{session.Id}', " +
                $"remote manifest generation={remoteManifest.Generation}. " +
                "Contract acquisition was rejected before any RPC payload exchange.");
        }

        if (remoteHash == localHash)
            return;

        throw new SharpLinkException(
            SharpLinkErrorCode.FailedPrecondition,
            $"RPC assembly compatibility mismatch for contract '{contract.ContractName}' ({contract.ContractId}). " +
            $"Local assembly='{localManifest.OwnerAssembly.FullName}', local RpcAssemblyHash='{localHash}'; " +
            $"remote RpcAssemblyHash='{remoteHash}', session='{session.Id}', " +
            $"remote manifest generation={remoteManifest.Generation}. " +
            "Contract acquisition was rejected before any RPC payload exchange.");
    }

    private readonly record struct RemoteContractManifestBinding(
        RpcSession Session,
        ProtocolV2ContractManifest Manifest);
}
