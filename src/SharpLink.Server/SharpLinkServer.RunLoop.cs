namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    public ValueTask RunAsync(CancellationToken cancellationToken = default)
        => _lifecycle.RunAsync(cancellationToken);

    private async Task RunAcceptLoopAsync(CancellationToken acceptToken)
    {
        LogServerCallCapacityConfigured(
            _logger,
            _maxConcurrentCallsPerConnection,
            _maxConcurrentCallsPerServer);
        LogServerConnectionAdmissionConfigured(
            _logger,
            _connectionAdmission.MaxConnections,
            _connectionAdmission.MaxHandshakes);
        StartDecodeExecutor();
        TrackFrameworkTask(
            RunHeartbeatCheckLoopAsync(_forceStopCts.Token),
            "HeartbeatCheckLoop");

        while (!acceptToken.IsCancellationRequested)
        {
            ITransportConnection? connection = null;
            try
            {
                connection = await _transportListener.AcceptAsync(acceptToken).ConfigureAwait(false);
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
                    HandleAcceptedConnectionAsync(connection, connectionLease, _forceStopCts.Token),
                    "AcceptedConnectionSession");
                connection = null;
            }
            catch (OperationCanceledException) when (acceptToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (
                acceptToken.IsCancellationRequested || CurrentState == ServerState.Draining)
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
    }

    private static bool IsExpectedCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;

    private static bool IsExpectedConnectionTermination(Exception ex, CancellationToken ct)
        => IsExpectedCancellation(ex, ct) ||
            ex is System.IO.IOException or ObjectDisposedException or System.Net.Sockets.SocketException or
            SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed };
}
