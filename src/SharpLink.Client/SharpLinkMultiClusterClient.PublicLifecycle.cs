namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    private Task? _runtimeStartTask;
    private int _lifecycleState = (int)SharpLinkClientLifecycleState.Created;

    public SharpLinkClientLifecycleState LifecycleState
    {
        get
        {
            var aggregateState = (SharpLinkMultiClusterState)Volatile.Read(ref _state);
            if (aggregateState == SharpLinkMultiClusterState.Draining)
                return SharpLinkClientLifecycleState.Draining;
            if (aggregateState == SharpLinkMultiClusterState.Stopped)
                return SharpLinkClientLifecycleState.Stopped;
            return (SharpLinkClientLifecycleState)Volatile.Read(ref _lifecycleState);
        }
    }

    public SharpLinkReadinessState Readiness
    {
        get
        {
            if (LifecycleState != SharpLinkClientLifecycleState.Running)
                return SharpLinkReadinessState.NotReady;

            var slots = Volatile.Read(ref _snapshot).Slots;
            if (slots.Length == 0)
                return SharpLinkReadinessState.Ready;

            var ready = 0;
            for (var index = 0; index < slots.Length; index++)
            {
                if (slots[index].Client.Readiness == SharpLinkReadinessState.Ready)
                    ready++;
            }
            if (ready == slots.Length)
                return SharpLinkReadinessState.Ready;
            return ready == 0
                ? SharpLinkReadinessState.NotReady
                : SharpLinkReadinessState.Degraded;
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        Task operation;
        var observe = false;
        lock (_gate)
        {
            var lifecycle = LifecycleState;
            if (lifecycle == SharpLinkClientLifecycleState.Running)
                return ValueTask.CompletedTask;
            if (lifecycle is SharpLinkClientLifecycleState.Draining or
                SharpLinkClientLifecycleState.Stopped or SharpLinkClientLifecycleState.Faulted)
            {
                return ValueTask.FromException(new InvalidOperationException(
                    $"Multi-cluster client lifecycle state '{lifecycle}' cannot start."));
            }

            if (_runtimeStartTask is not { IsCompleted: false })
            {
                Volatile.Write(ref _lifecycleState, (int)SharpLinkClientLifecycleState.Starting);
                _runtimeStartTask = StartRuntimeCoreAsync();
                observe = true;
            }
            operation = _runtimeStartTask ??
                throw new InvalidOperationException("Coordinator startup has no owned start operation.");
        }

        if (observe)
            ObserveBackgroundFailure(operation);
        return cancellationToken.CanBeCanceled
            ? new ValueTask(operation.WaitAsync(cancellationToken))
            : new ValueTask(operation);
    }

    public ValueTask WaitForReadyAsync(CancellationToken cancellationToken = default)
    {
        if (Readiness == SharpLinkReadinessState.Ready)
            return ValueTask.CompletedTask;
        return new ValueTask(WaitForReadyCoreAsync(cancellationToken));
    }

    public ValueTask WaitForReadyAsync(
        SharpLinkClusterKey cluster,
        CancellationToken cancellationToken = default)
    {
        var slot = GetSlot(cluster);
        return slot.Client.WaitForReadyAsync(cancellationToken);
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (LifecycleState == SharpLinkClientLifecycleState.Stopped)
            return Task.CompletedTask;
        return WaitForShutdownCoreAsync(cancellationToken);
    }

    public SharpLinkClusterState GetClusterRuntimeState(SharpLinkClusterKey cluster)
        => GetSlot(cluster).Client.ClusterState;

    public SharpLinkReadinessState GetClusterReadiness(SharpLinkClusterKey cluster)
        => GetSlot(cluster).Client.Readiness;

    private async Task StartRuntimeCoreAsync()
    {
        var ownsMutationGate = false;
        try
        {
            await _mutationGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            ownsMutationGate = true;
            var slots = Volatile.Read(ref _snapshot).Slots;
            await Parallel.ForEachAsync(
                slots,
                new ParallelOptions
                {
                    CancellationToken = _shutdown.Token,
                    MaxDegreeOfParallelism = _options.MaxConcurrentClusterConnects
                },
                static async (slot, token) =>
                    await slot.Client.StartAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
            _ = Interlocked.CompareExchange(
                ref _state,
                (int)SharpLinkMultiClusterState.Degraded,
                (int)SharpLinkMultiClusterState.Created);
            Volatile.Write(ref _lifecycleState, (int)SharpLinkClientLifecycleState.Running);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception startException)
        {
            Volatile.Write(ref _lifecycleState, (int)SharpLinkClientLifecycleState.Faulted);
            var failures = new List<Exception> { startException };
            await StopSlotsAsync(Volatile.Read(ref _snapshot).Slots, failures).ConfigureAwait(false);
            _ = Interlocked.CompareExchange(
                ref _state,
                (int)SharpLinkMultiClusterState.Faulted,
                (int)SharpLinkMultiClusterState.Created);
            if (failures.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startException).Throw();
            throw new AggregateException(failures);
        }
        finally
        {
            if (ownsMutationGate)
                _mutationGate.Release();
        }
    }

    private async Task WaitForReadyCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lifecycle = LifecycleState;
            if (lifecycle == SharpLinkClientLifecycleState.Created)
            {
                throw new InvalidOperationException(
                    "StartAsync must be called before waiting for multi-cluster readiness.");
            }
            if (lifecycle == SharpLinkClientLifecycleState.Starting)
            {
                Task? startTask;
                lock (_gate)
                    startTask = _runtimeStartTask;
                if (startTask is null)
                    throw new InvalidOperationException("Coordinator startup has no owned start operation.");
                await startTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (lifecycle is SharpLinkClientLifecycleState.Draining or SharpLinkClientLifecycleState.Stopped)
                throw new InvalidOperationException("The multi-cluster client stopped before becoming ready.");
            if (lifecycle == SharpLinkClientLifecycleState.Faulted)
                throw new InvalidOperationException("The local multi-cluster coordinator is faulted.");

            var snapshot = Volatile.Read(ref _snapshot);
            var slots = snapshot.Slots;
            if (slots.Length == 0)
                return;
            var waits = new Task[slots.Length];
            for (var index = 0; index < slots.Length; index++)
                waits[index] = slots[index].Client.WaitForReadyAsync(cancellationToken).AsTask();
            try
            {
                await Task.WhenAll(waits).ConfigureAwait(false);
            }
            catch when (!ReferenceEquals(snapshot, Volatile.Read(ref _snapshot)))
            {
                continue;
            }

            if (ReferenceEquals(snapshot, Volatile.Read(ref _snapshot)) &&
                Readiness == SharpLinkReadinessState.Ready)
            {
                return;
            }
        }
    }

    private async Task WaitForShutdownCoreAsync(CancellationToken cancellationToken)
    {
        CancellationToken shutdownToken = default;
        var canWaitForSignal = true;
        try
        {
            shutdownToken = _shutdown.Token;
        }
        catch (ObjectDisposedException)
        {
            canWaitForSignal = false;
        }

        if (canWaitForSignal && !shutdownToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, shutdownToken)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
            }
        }

        Task? stopTask;
        lock (_gate)
            stopTask = _stopTask;
        if (stopTask is null)
        {
            if (LifecycleState == SharpLinkClientLifecycleState.Stopped)
                return;
            throw new InvalidOperationException(
                "Coordinator shutdown was signaled without an owned stop operation.");
        }

        if (cancellationToken.CanBeCanceled)
            await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        else
            await stopTask.ConfigureAwait(false);
    }
}
