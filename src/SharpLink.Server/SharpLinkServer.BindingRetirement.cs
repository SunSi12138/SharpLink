#if SHARPLINK_775_STATIC_SLICE
using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    // One cold transaction owns the cut for all services and all physical Sessions.
    // The callback is a profile-only deterministic barrier, never a product notification.
    internal Action? AfterBindingGroupCutTestHook;

    private BindingRetirementBatch[] PrepareBindingRetirement(SharpLinkDynamicModule module)
        => _connectionRegistry.SnapshotOwned()
            .Select(connection => new BindingRetirementBatch(connection, connection.StaticBindings.PrepareRetirement(module)))
            .Where(batch => batch.Tokens.Length != 0).ToArray();

    private void CommitBindingCut(SharpLinkDynamicModule module, BindingRetirementBatch[] batches)
    {
        module.TryBeginDraining(); // Existing striped lease state is the shared group admission gate.
        foreach (var batch in batches)
            foreach (var token in batch.Tokens) batch.Connection.StaticBindings.Release(token);
        AfterBindingGroupCutTestHook?.Invoke();
    }

    private static void PublishBindingRetirement(BindingRetirementBatch[] batches)
    {
        foreach (var batch in batches)
        {
            try { StaticBindingWire.SendInvalidation(batch.Connection.Session, batch.Tokens); }
            catch (SharpLinkException failure) when (failure.Code is SharpLinkErrorCode.ConnectionClosed or SharpLinkErrorCode.ResourceExhausted)
            { batch.Connection.Session.NotifyDisconnected(failure); }
        }
    }

    private ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterBoundAssemblyAsync(Assembly assembly,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        SharpLinkDynamicModule module;
        TaskCompletionSource<SharpLinkAssemblyUnregisterResult> completion;
        BindingRetirementBatch[] batches;
        lock (_registryGate)
        {
            if (_unregisterOperations.TryGetValue(assembly, out var pending))
                return WaitForUnregisterAsync(pending, cancellationToken);
            if (!_dynamicModules.TryGetValue(assembly, out module!))
                return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
            EnsureNoDynamicDependants(module);
            if (module.State != SharpLinkDynamicModuleState.Running)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _unregisterOperations.Add(assembly, completion.Task);
                TrackFrameworkTask(completion.Task, "BoundAssemblyDrainJoin", TaskObservationMode.ExternallyObserved);
                _ = CompleteUnregisterOperationAsync(assembly, module, timeout, completion);
                return WaitForUnregisterAsync(completion.Task, cancellationToken);
            }
            var current = Volatile.Read(ref _services);
            var detached = current.Values.Where(service => ReferenceEquals(service.Module, module)).ToArray();
            var next = current.Where(pair => !ReferenceEquals(pair.Value.Module, module))
                .ToDictionary(pair => pair.Key, pair => pair.Value).ToFrozenDictionary();
            batches = PrepareBindingRetirement(module);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _unregisterOperations.Add(assembly, completion.Task);
            _detachedModuleServices.Add(module, detached);
            CommitBindingCut(module, batches);
            Volatile.Write(ref _services, next);
            _registryGeneration++;
        }
        // No fallible publish/scheduling operation is inside the irreversible cut.
        PublishBindingRetirement(batches);
        ScheduleContractManifestPublish();
        TrackFrameworkTask(completion.Task, "BoundAssemblyUnregister", TaskObservationMode.ExternallyObserved);
        _ = CompleteUnregisterOperationAsync(assembly, module, timeout, completion);
        return WaitForUnregisterAsync(completion.Task, cancellationToken);
    }

    private sealed record BindingRetirementBatch(ServerConnectionState Connection, ulong[] Tokens);

    private async Task ReleaseModuleAsync(Assembly assembly, SharpLinkDynamicModule module)
    {
        if (Interlocked.CompareExchange(ref module.BindingReleaseStarted, 1, 0) != 0)
        {
            await module.BindingPhysicalReleased.Task.ConfigureAwait(false);
            return;
        }
        try
        {
            await ReleaseBoundModuleCoreAsync(assembly, module).ConfigureAwait(false);
            module.BindingPhysicalReleased.TrySetResult();
        }
        catch (Exception failure) { module.BindingPhysicalReleased.TrySetException(failure); throw; }
    }
}
#endif

