namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private readonly Lock _lifecycleGate = new();
    private Task? _startTask;
    private int _lifecycleState = (int)SharpLinkClientLifecycleState.Created;

    public SharpLinkClientLifecycleState LifecycleState
    {
        get
        {
            var connectionState = State;
            if (connectionState == SharpLinkConnectionState.Draining)
                return SharpLinkClientLifecycleState.Draining;
            if (connectionState == SharpLinkConnectionState.Stopped)
                return SharpLinkClientLifecycleState.Stopped;
            return (SharpLinkClientLifecycleState)Volatile.Read(ref _lifecycleState);
        }
    }

    public SharpLinkReadinessState Readiness =>
        LifecycleState == SharpLinkClientLifecycleState.Running && ReadyConnectionCount != 0
            ? SharpLinkReadinessState.Ready
            : SharpLinkReadinessState.NotReady;

    public SharpLinkClusterState ClusterState => State switch
    {
        SharpLinkConnectionState.Created => SharpLinkClusterState.Inactive,
        SharpLinkConnectionState.Connecting => SharpLinkClusterState.Connecting,
        SharpLinkConnectionState.Ready => SharpLinkClusterState.Ready,
        SharpLinkConnectionState.Draining => SharpLinkClusterState.Draining,
        SharpLinkConnectionState.Reconnecting => SharpLinkClusterState.Reconnecting,
        SharpLinkConnectionState.Stopped => SharpLinkClusterState.Stopped,
        SharpLinkConnectionState.Faulted => SharpLinkClusterState.Unavailable,
        _ => SharpLinkClusterState.Unavailable
    };

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        Task operation;
        lock (_lifecycleGate)
        {
            var state = LifecycleState;
            if (state == SharpLinkClientLifecycleState.Running)
                return ValueTask.CompletedTask;
            if (state is SharpLinkClientLifecycleState.Draining or
                SharpLinkClientLifecycleState.Stopped or SharpLinkClientLifecycleState.Faulted)
            {
                return ValueTask.FromException(CreateConnectionClosedException(
                    $"Client lifecycle state '{state}' cannot start."));
            }

            if (_startTask is not { IsCompleted: false })
            {
                Volatile.Write(ref _lifecycleState, (int)SharpLinkClientLifecycleState.Starting);
                try
                {
                    _startTask = StartRuntimeCore();
                }
                catch
                {
                    Volatile.Write(ref _lifecycleState, (int)SharpLinkClientLifecycleState.Faulted);
                    throw;
                }
            }
            operation = _startTask ??
                throw new InvalidOperationException("Client startup has no owned start operation.");
        }

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

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (LifecycleState == SharpLinkClientLifecycleState.Stopped)
            return Task.CompletedTask;
        return WaitForShutdownCoreAsync(cancellationToken);
    }

    private Task StartRuntimeCore()
    {
        if (_shutdownCts.IsCancellationRequested)
            throw CreateConnectionClosedException("Client shutdown has already started.");

        var supervisor = RunInitialConnectivitySupervisorAsync();
        TrackFrameworkTask(supervisor, "InitialConnectivitySupervisor");
        Volatile.Write(ref _lifecycleState, (int)SharpLinkClientLifecycleState.Running);
        return Task.CompletedTask;
    }

    private async Task RunInitialConnectivitySupervisorAsync()
    {
        var delay = CaptureReconnectPolicy().Policy.InitialDelay;
        while (!_shutdownCts.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(_shutdownCts.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogClientConnectionAttemptFailed(
                    _logger,
                    nameof(RunInitialConnectivitySupervisorAsync),
                    exception);
                if (!_shutdownCts.IsCancellationRequested)
                {
                    _ = Interlocked.CompareExchange(
                        ref _state,
                        (int)SharpLinkConnectionState.Reconnecting,
                        (int)SharpLinkConnectionState.Faulted);
                }
            }

            try
            {
                var generation = CaptureReconnectPolicy();
                if (await WaitForReconnectDelayAsync(
                        delay,
                        generation,
                        _shutdownCts.Token).ConfigureAwait(false))
                {
                    delay = NextReconnectDelay(delay, generation.Policy);
                }
                else
                {
                    delay = CaptureReconnectPolicy().Policy.InitialDelay;
                }
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                return;
            }
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
                    "StartAsync must be called before waiting for client readiness.");
            }
            if (lifecycle == SharpLinkClientLifecycleState.Starting)
            {
                Task? startTask;
                lock (_lifecycleGate)
                    startTask = _startTask;
                if (startTask is null)
                    throw new InvalidOperationException("Client startup has no owned start operation.");
                await startTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (lifecycle is SharpLinkClientLifecycleState.Draining or SharpLinkClientLifecycleState.Stopped)
                throw CreateConnectionClosedException("Client stopped before becoming ready.");
            if (lifecycle == SharpLinkClientLifecycleState.Faulted)
                throw new InvalidOperationException("The local client runtime is faulted.");
            if (ReadyConnectionCount != 0)
                return;

            var readySignal = Volatile.Read(ref _readySignal).Task;
            if (ReadyConnectionCount != 0)
                return;
            await readySignal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForShutdownCoreAsync(CancellationToken cancellationToken)
    {
        CancellationToken shutdownToken = default;
        var canWaitForSignal = true;
        try
        {
            shutdownToken = _shutdownCts.Token;
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
        lock (_stateGate)
            stopTask = _stopTask;
        if (stopTask is null)
        {
            if (LifecycleState == SharpLinkClientLifecycleState.Stopped)
                return;
            throw new InvalidOperationException("Client shutdown was signaled without an owned stop operation.");
        }

        if (cancellationToken.CanBeCanceled)
            await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        else
            await stopTask.ConfigureAwait(false);
    }
}
