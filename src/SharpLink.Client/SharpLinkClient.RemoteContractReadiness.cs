namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    // The signal owns no sessions, assemblies, proxies or waiter registrations. WaitAsync removes
    // each canceled caller's continuation; unrelated waiters and connectivity continue unchanged.
    private TaskCompletionSource _remoteContractChanged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask WaitForRemoteContractAsync(
        long contractId,
        RpcHash128 rpcAssemblyHash,
        CancellationToken cancellationToken = default)
    {
        if (contractId == 0)
            throw new ArgumentOutOfRangeException(nameof(contractId), "A nonzero wire contract ID is required.");
        if (rpcAssemblyHash.IsEmpty)
            throw new ArgumentException("An exact nonempty RPC assembly hash is required.", nameof(rpcAssemblyHash));
        cancellationToken.ThrowIfCancellationRequested();
        return WaitForRemoteContractCoreAsync(contractId, rpcAssemblyHash, null, cancellationToken);
    }

    public ValueTask WaitForRemoteContractAsync(
        long contractId,
        RpcHash128 rpcAssemblyHash,
        RpcHash128 previousRpcAssemblyHash,
        CancellationToken cancellationToken = default)
    {
        if (contractId == 0)
            throw new ArgumentOutOfRangeException(nameof(contractId), "A nonzero wire contract ID is required.");
        if (rpcAssemblyHash.IsEmpty)
            throw new ArgumentException("An exact nonempty RPC assembly hash is required.", nameof(rpcAssemblyHash));
        if (previousRpcAssemblyHash.IsEmpty)
            throw new ArgumentException("An exact nonempty previous RPC assembly hash is required.", nameof(previousRpcAssemblyHash));
        cancellationToken.ThrowIfCancellationRequested();
        return WaitForRemoteContractCoreAsync(contractId, rpcAssemblyHash, previousRpcAssemblyHash, cancellationToken);
    }

    private async ValueTask WaitForRemoteContractCoreAsync(
        long contractId,
        RpcHash128 rpcAssemblyHash,
        RpcHash128? previousRpcAssemblyHash,
        CancellationToken cancellationToken)
    {
        await WaitForReadinessAsync(1, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            // Capture the signal BEFORE inspecting facts so publication cannot lose a wakeup.
            var changed = Volatile.Read(ref _remoteContractChanged);
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _stopStarted) != 0)
                throw CreateConnectionClosedException("Client stopped before remote contract readiness was observed.");
            var readiness = GetReadinessSnapshot();
            ThrowIfReadinessWaitCannotContinue(readiness.State);
            var readyConnections = 0;
            var missing = false;
            var bindings = Volatile.Read(ref _remoteContractManifestSnapshot);
            for (var index = 0; index < bindings.Length; index++)
            {
                var binding = bindings[index];
                if (!binding.Session.IsConnected || binding.Session.ProtocolPhase != RpcSessionProtocolPhase.Ready)
                    continue;
                readyConnections++;
                if (!binding.Manifest.Contracts.TryGetValue(contractId, out var remoteHash))
                {
                    missing = true;
                    continue;
                }
                if (remoteHash != rpcAssemblyHash)
                {
                    if (remoteHash == previousRpcAssemblyHash)
                    {
                        missing = true;
                        continue;
                    }
                    throw new SharpLinkException(
                        SharpLinkErrorCode.FailedPrecondition,
                        $"RPC assembly compatibility mismatch for contract {contractId}. " +
                        $"Local RpcAssemblyHash='{rpcAssemblyHash}', remote RpcAssemblyHash='{remoteHash}', " +
                        $"session='{binding.Session.Id}', remote manifest generation={binding.Manifest.Generation}.");
                }
            }

            if (!missing && readyConnections > 0 && readiness.State == SharpLinkConnectionState.Ready &&
                readyConnections >= readiness.ReadyConnections)
                return;
            await changed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void PulseRemoteContractWaiters()
        => Interlocked.Exchange(ref _remoteContractChanged,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}
