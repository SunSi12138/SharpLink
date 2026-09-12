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

        var isInitial = false;
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
                isInitial = true;
            }

            var snapshot = new RemoteContractManifestBinding[_remoteContractManifests.Count];
            var index = 0;
            foreach (var pair in _remoteContractManifests)
                snapshot[index++] = new RemoteContractManifestBinding(pair.Key, pair.Value);
            Volatile.Write(ref _remoteContractManifestSnapshot, snapshot);
        }

        // Get<T>() historically supports pre-connection proxy acquisition. The first manifest
        // for a session validates every proxy that already escaped to user code before that
        // connection is published as callable. Later manifest refreshes intentionally do not
        // rebind or invalidate held proxy references; later Get<T>() calls validate the refresh.
        if (isInitial)
            ValidateAcquiredContractAssemblies();
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

    private void ValidateAcquiredContractAssemblies()
    {
        foreach (var registration in _assemblyRegistry.CaptureProxySnapshot().Values)
        {
            if (Volatile.Read(ref registration.Proxy) is null)
                continue;
            ValidateRemoteContractAssembly(registration);
        }
    }

    private void ValidateRemoteContractAssembly(ClientProxyRegistration registration)
    {
        var contract = registration.Descriptor;
        var localManifest = FindOwningManifest(registration);
        var localHash = localManifest.RpcAssemblyHash;
        if (localHash.IsEmpty)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.FailedPrecondition,
                $"RPC contract '{contract.ContractName}' ({contract.ContractId}) is owned by assembly " +
                $"'{localManifest.OwnerAssembly.FullName}', but its local RpcAssemblyHash is empty.");
        }

        var manifests = Volatile.Read(ref _remoteContractManifestSnapshot);
        for (var index = 0; index < manifests.Length; index++)
        {
            var binding = manifests[index];
            var session = binding.Session;
            if (!session.IsConnected || session.ProtocolPhase != RpcSessionProtocolPhase.Ready)
                continue;

            ValidateRemoteContractAssembly(
                registration,
                localManifest,
                localHash,
                session,
                binding.Manifest);
        }

        // With no ready remote identity yet, preserve the existing synchronous API: callers may
        // acquire a proxy before ConnectAsync. Initial-manifest publication validates such proxies
        // before the connection becomes available to calls.
    }

    private ISharpLinkGeneratedAssemblyManifest FindOwningManifest(ClientProxyRegistration registration)
        => _assemblyRegistry.FindOwningManifest(registration);

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
