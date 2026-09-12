namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private int _contractManifestPublishScheduled;

    private ProtocolV2ContractManifest CreateContractManifestSnapshot()
    {
        lock (_registryGate)
            return CreateContractManifestSnapshotLocked();
    }

    private ProtocolV2ContractManifest CreateContractManifestSnapshotLocked()
    {
        var modules = _dynamicModules.Values.ToArray();
        var services = Volatile.Read(ref _services);
        var entries = new KeyValuePair<long, RpcHash128>[services.Count];
        var index = 0;
        foreach (var service in services.OrderBy(static pair => pair.Key))
        {
            var manifest = FindManifest(service.Value.ContractType.Assembly, modules);
            entries[index++] = new KeyValuePair<long, RpcHash128>(
                service.Key,
                manifest.RpcAssemblyHash);
        }
        return new ProtocolV2ContractManifest(_registryGeneration, entries);
    }

    private void ScheduleContractManifestPublish()
    {
        // Before Running there are no published callable sessions; their initial handshake reads
        // the current registry snapshot. Once draining starts, registry cleanup must not enqueue
        // fresh framework work or perturb shutdown failure aggregation.
        if (CurrentState != ServerState.Running)
            return;
        if (Interlocked.Exchange(ref _contractManifestPublishScheduled, 1) != 0)
            return;
        TrackFrameworkTask(PublishContractManifestUpdatesAsync(), "ContractManifestPublish");
    }

    private async Task PublishContractManifestUpdatesAsync()
    {
        await Task.Yield();
        long publishedGeneration = -1;
        try
        {
            while (CurrentState == ServerState.Running)
            {
                var snapshot = CreateContractManifestSnapshot();
                publishedGeneration = snapshot.Generation;
                foreach (var connection in _connectionRegistry.SnapshotActive())
                {
                    if (connection.LifecycleState != ServerConnectionLifecycleState.Ready ||
                        (connection.Session.NegotiatedCapabilities & ProtocolV2Capabilities.ContractManifest) == 0)
                    {
                        continue;
                    }
                    try
                    {
                        connection.Session.SendContractManifest(snapshot);
                    }
                    catch (Exception exception) when (IsExpectedSessionShutdownException(exception))
                    {
                    }
                }

                if (Volatile.Read(ref _registryGeneration) == publishedGeneration)
                    return;
                await Task.Yield();
            }
        }
        finally
        {
            Volatile.Write(ref _contractManifestPublishScheduled, 0);
            if (CurrentState == ServerState.Running &&
                Volatile.Read(ref _registryGeneration) != publishedGeneration)
            {
                ScheduleContractManifestPublish();
            }
        }
    }
}
