namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    public ValueTask RunAsync(CancellationToken cancellationToken = default)
    {
        Task runTask;
        lock (_stateGate)
        {
            if (_runTask is null)
            {
                if (CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
                    return ValueTask.FromException(new SharpLinkException(
                        SharpLinkErrorCode.ConnectionClosed,
                        "Server cannot be restarted."));
                _runTask = RunCoreAsync(cancellationToken);
            }
            runTask = _runTask;
        }
        return new ValueTask(runTask);
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        TransitionTo(ServerState.Starting);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _acceptCts.Token);
        var acceptToken = runCts.Token;
        TransitionTo(ServerState.Running);
        LogServerCallCapacityConfigured(
            _logger,
            _maxConcurrentCallsPerConnection,
            _maxConcurrentCallsPerServer);
        TrackFrameworkTask(RunHeartbeatCheckLoopAsync(_forceStopCts.Token));

        try
        {
            while (!acceptToken.IsCancellationRequested)
            {
                ITransportConnection? connection = null;
                try
                {
                    connection = await transportListener.AcceptAsync(acceptToken).ConfigureAwait(false);
                    TrackFrameworkTask(HandleAcceptedConnectionAsync(connection, _forceStopCts.Token));
                    connection = null;
                }
                catch (OperationCanceledException) when (acceptToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (acceptToken.IsCancellationRequested || CurrentState == ServerState.Draining)
                {
                    break;
                }
                catch
                {
                    if (connection is not null)
                        await connection.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            if (cancellationToken.IsCancellationRequested && CurrentState == ServerState.Running)
            {
                Task stopTask;
                lock (_stateGate)
                {
                    _stopTask ??= StopCoreAsync(TimeSpan.Zero);
                    stopTask = _stopTask;
                }
                await stopTask.ConfigureAwait(false);
            }
            else
            {
                Task? stopTask;
                lock (_stateGate)
                    stopTask = _stopTask;
                if (stopTask is not null)
                    await stopTask.ConfigureAwait(false);
            }
        }
        catch
        {
            TransitionTo(ServerState.Faulted);
            Task cleanupTask;
            lock (_stateGate)
            {
                _stopTask ??= CleanupAfterRunFailureAsync();
                cleanupTask = _stopTask;
            }
            await cleanupTask.ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsExpectedCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;

    private static bool IsExpectedConnectionTermination(Exception ex, CancellationToken ct)
        => IsExpectedCancellation(ex, ct) ||
            ex is System.IO.IOException or ObjectDisposedException or System.Net.Sockets.SocketException or
            SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed };
}
