namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private async Task RunAcceptLoopAsync(
        TaskCompletionSource<bool> acceptStarted,
        TaskCompletionSource<bool> running,
        CancellationToken acceptToken)
    {
        try
        {
            LogServerCallCapacityConfigured(
                _logger,
                _maxConcurrentCallsPerConnection,
                _maxConcurrentCallsPerServer);
            var connectionAdmissionTargets = _connectionAdmission.TargetSnapshot;
            LogServerConnectionAdmissionConfigured(
                _logger,
                connectionAdmissionTargets.MaxConnections,
                connectionAdmissionTargets.MaxHandshakes);
            StartDecodeExecutor();
            TrackServerRuntimeTask(
                RunHeartbeatCheckLoopAsync(_forceStopCts.Token),
                "HeartbeatCheckLoop");

            while (!acceptToken.IsCancellationRequested)
            {
                ITransportConnection? connection = null;
                try
                {
                    var accept = _transportListener.AcceptAsync(acceptToken);
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

                    if (!_connectionAdmission.TryAcquireConnection(out var connectionLease))
                    {
                        RecordConnectionAdmissionRejection(ConnectionAdmissionRejectionReason.ConnectionLimit);
                        try
                        {
                            await connection.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            // A rejected transport must never take down the accept loop;
                            // the failure is observed without terminating the listener.
                            LogDeferredCleanupFailed(_logger, "ConnectionAdmissionReject", exception);
                        }
                        continue;
                    }

                    TrackFrameworkTask(
                        RunAcceptedConnectionIsolatedAsync(
                            connection,
                            connectionLease,
                            _forceStopCts.Token),
                        "AcceptedConnectionSession",
                        TaskObservationMode.ExternallyObserved);
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

    private async Task RunAcceptedConnectionIsolatedAsync(
        ITransportConnection connection,
        ServerConnectionAdmission.Lease connectionLease,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleAcceptedConnectionAsync(connection, connectionLease, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsExpectedCancellation(exception, cancellationToken))
        {
            LogDeferredCleanupFailed(_logger, "AcceptedConnectionSession", exception);
        }
    }

    private static bool IsExpectedCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;

    private static bool IsExpectedConnectionTermination(Exception ex, CancellationToken ct)
        => IsExpectedCancellation(ex, ct) ||
            ex is System.IO.IOException or ObjectDisposedException or System.Net.Sockets.SocketException or
            SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed };
}
