namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    public SharpLinkServerLifecycleState LifecycleState => CurrentState switch
    {
        ServerState.Created => SharpLinkServerLifecycleState.Created,
        ServerState.Starting => SharpLinkServerLifecycleState.Starting,
        ServerState.Running => SharpLinkServerLifecycleState.Running,
        ServerState.Draining => SharpLinkServerLifecycleState.Draining,
        ServerState.Stopped => SharpLinkServerLifecycleState.Stopped,
        ServerState.Faulted => SharpLinkServerLifecycleState.Faulted,
        _ => SharpLinkServerLifecycleState.Faulted
    };

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task operation;
        TaskCompletionSource<bool>? startCompletion = null;
        lock (_stateGate)
        {
            var state = CurrentState;
            if (state == ServerState.Running)
                return ValueTask.CompletedTask;
            if (state is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
                return ValueTask.FromException(new SharpLinkException(
                    SharpLinkErrorCode.ConnectionClosed,
                    $"Server lifecycle state '{state}' cannot start."));
            }

            if (state == ServerState.Starting)
            {
                operation = _startTask ??
                    throw new InvalidOperationException("Server startup has no owned start operation.");
            }
            else
            {
                TransitionTo(ServerState.Starting);
                startCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _startTask = startCompletion.Task;
                operation = _startTask;
            }
        }

        if (startCompletion is not null)
        {
            _ = CompleteStartAsync(startCompletion, cancellationToken);
            return new ValueTask(operation);
        }
        return cancellationToken.CanBeCanceled
            ? new ValueTask(operation.WaitAsync(cancellationToken))
            : new ValueTask(operation);
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        var completion = _terminalCompletion.Task;
        return cancellationToken.CanBeCanceled
            ? completion.WaitAsync(cancellationToken)
            : completion;
    }

    private async Task CompleteStartAsync(
        TaskCompletionSource<bool> completion,
        CancellationToken startupCancellation)
    {
        try
        {
            await StartCoreAsync(startupCancellation).ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (OperationCanceledException exception) when (startupCancellation.IsCancellationRequested)
        {
            try
            {
                await StopAsync(TimeSpan.Zero).ConfigureAwait(false);
                completion.TrySetCanceled(startupCancellation);
            }
            catch (Exception cleanupException)
            {
                completion.TrySetException(new AggregateException(exception, cleanupException));
            }
        }
        catch (Exception exception)
        {
            await BeginTerminalFailure(exception).ConfigureAwait(false);
            completion.TrySetException(exception);
        }
    }

    private async Task StartCoreAsync(CancellationToken startupCancellation)
    {
        var acceptStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptToken = _acceptCts.Token;

        LogServerCallCapacityConfigured(
            _logger,
            _maxConcurrentCallsPerConnection,
            _maxConcurrentCallsPerServer);
        TrackServerRuntimeTask(RunHeartbeatCheckLoopAsync(_forceStopCts.Token));

        var acceptTask = RunAcceptLoopAsync(acceptStarted, running, acceptToken);
        Volatile.Write(ref _acceptTask, acceptTask);

        try
        {
            await acceptStarted.Task.WaitAsync(startupCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            acceptToken.IsCancellationRequested && !startupCancellation.IsCancellationRequested)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ConnectionClosed,
                "Server stopped while startup was in progress.");
        }

        startupCancellation.ThrowIfCancellationRequested();
        var previous = (ServerState)Interlocked.CompareExchange(
            ref _state,
            (int)ServerState.Running,
            (int)ServerState.Starting);
        if (previous != ServerState.Starting)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ConnectionClosed,
                $"Server startup was interrupted by lifecycle state '{previous}'.");
        }
        running.TrySetResult(true);

        if (acceptTask.IsCompleted)
        {
            await acceptTask.ConfigureAwait(false);
            throw new InvalidOperationException("Server accept loop completed during startup.");
        }

        Volatile.Write(ref _acceptObserverTask, ObserveAcceptLoopAsync(acceptTask, acceptToken));
    }

    private async Task RunAcceptLoopAsync(
        TaskCompletionSource<bool> acceptStarted,
        TaskCompletionSource<bool> running,
        CancellationToken acceptToken)
    {
        try
        {
            while (!acceptToken.IsCancellationRequested)
            {
                ITransportConnection? connection = null;
                try
                {
                    var accept = transportListener.AcceptAsync(acceptToken);
                    if (!accept.IsCompleted)
                        acceptStarted.TrySetResult(true);
                    connection = await accept.ConfigureAwait(false);
                    acceptStarted.TrySetResult(true);

                    if (CurrentState == ServerState.Starting)
                        await running.Task.WaitAsync(acceptToken).ConfigureAwait(false);
                    if (CurrentState != ServerState.Running)
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                        connection = null;
                        break;
                    }

                    TrackFrameworkTask(HandleAcceptedConnectionAsync(connection, _forceStopCts.Token));
                    connection = null;
                }
                catch (OperationCanceledException) when (acceptToken.IsCancellationRequested)
                {
                    if (connection is not null)
                        await connection.DisposeAsync().ConfigureAwait(false);
                    acceptStarted.TrySetCanceled(acceptToken);
                    break;
                }
                catch (ObjectDisposedException) when (
                    acceptToken.IsCancellationRequested ||
                    CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
                {
                    if (connection is not null)
                        await connection.DisposeAsync().ConfigureAwait(false);
                    acceptStarted.TrySetCanceled(acceptToken);
                    break;
                }
                catch
                {
                    if (connection is not null)
                        await connection.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        catch (Exception exception)
        {
            acceptStarted.TrySetException(exception);
            throw;
        }
        finally
        {
            if (!acceptStarted.Task.IsCompleted)
                acceptStarted.TrySetCanceled(acceptToken);
        }
    }

    private async Task ObserveAcceptLoopAsync(Task acceptTask, CancellationToken acceptToken)
    {
        try
        {
            await acceptTask.ConfigureAwait(false);
            if (CurrentState is ServerState.Starting or ServerState.Running)
            {
                _ = BeginTerminalFailure(new InvalidOperationException(
                    "Server accept loop completed unexpectedly."));
            }
        }
        catch (OperationCanceledException) when (acceptToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (
            CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
        {
        }
        catch (Exception exception)
        {
            _ = BeginTerminalFailure(exception);
        }
    }

    private Task BeginTerminalFailure(Exception failure)
    {
        lock (_stateGate)
        {
            var state = CurrentState;
            if (state is ServerState.Draining or ServerState.Stopped)
                return Task.CompletedTask;

            _terminalFailure ??= failure;
            if (state != ServerState.Faulted)
                TransitionTo(ServerState.Faulted);
            return _stopTask ??= CompleteFaultedStopAsync();
        }
    }

    private async Task CompleteFaultedStopAsync()
    {
        Exception? cleanupFailure = null;
        try
        {
            await CleanupAfterRunFailureAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        Exception terminalFailure;
        lock (_stateGate)
        {
            var primaryFailure = _terminalFailure ?? new SharpLinkException(
                SharpLinkErrorCode.Internal,
                "Server entered Faulted without a recorded terminal failure.");
            terminalFailure = cleanupFailure is null
                ? primaryFailure
                : new AggregateException(primaryFailure, cleanupFailure);
        }
        Volatile.Write(ref _terminalFailure, terminalFailure);
        _terminalCompletion.TrySetException(terminalFailure);
    }

    private async Task WaitForAcceptRuntimeShutdownAsync()
    {
        var observer = Volatile.Read(ref _acceptObserverTask);
        if (observer is not null)
        {
            await observer.ConfigureAwait(false);
            return;
        }

        var acceptTask = Volatile.Read(ref _acceptTask);
        if (acceptTask is null)
            return;
        try
        {
            await acceptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_acceptCts.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (
            CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
        {
        }
        catch (Exception) when (CurrentState == ServerState.Faulted)
        {
            // The raw accept failure is already recorded as the terminal failure.
        }
    }

}
