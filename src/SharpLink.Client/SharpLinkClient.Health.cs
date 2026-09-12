namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <inheritdoc />
    public async ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfHealthProbeCannotRun();
        if (!TryGetHealthProbeConnection(out var connection))
            return SharpLinkHealthCheckResult.NotReady;

        var session = connection.Session;
        if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.HealthCheck) == 0)
            return SharpLinkHealthCheckResult.Unsupported;

        var timeProvider = _runtimeContext.TimeProvider;
        var deadline = _hasRequestTimeout
            ? RpcDeadline.Create(_requestTimeoutValue, timeProvider)
            : default;
        try
        {
            var operation = connection.PendingCalls.Rent(
                HealthResponseCodec.Instance,
                PendingCallKind.Health,
                deadline,
                cancellationToken,
                out var requestId);
            try
            {
                if (connection.PendingCalls.Contains(requestId))
                    session.SendHealthCheck(requestId);
            }
            catch (Exception exception)
            {
                connection.PendingCalls.TryComplete(
                    requestId,
                    PendingCallCompletionReason.SendFailure,
                    exception);
            }

            return await operation.AsValueTask().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsHealthProbeUnavailable(exception))
        {
            return SharpLinkHealthCheckResult.Unavailable;
        }
    }

    private void ThrowIfHealthProbeCannotRun()
    {
        if (_shutdownCts.IsCancellationRequested ||
            State is SharpLinkConnectionState.Draining or SharpLinkConnectionState.Stopped)
        {
            throw CreateConnectionClosedException("Client is not accepting health probes.");
        }
        if (State == SharpLinkConnectionState.Faulted)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "Client connectivity has faulted.");
        }
    }

    private bool TryGetHealthProbeConnection(out ClientConnection connection)
    {
        var connections = _cluster is null
            ? Volatile.Read(ref _readyConnections)
            : _cluster.CaptureReadyConnections();
        if (connections.Length == 0)
        {
            connection = null!;
            return false;
        }

        var start = connections.Length == 1 ? 0 : Random.Shared.Next(connections.Length);
        for (var offset = 0; offset < connections.Length; offset++)
        {
            var candidate = connections[(start + offset) % connections.Length];
            if (!candidate.CanAcceptCalls)
                continue;

            connection = candidate;
            return true;
        }

        connection = null!;
        return false;
    }

    private static bool IsHealthProbeUnavailable(Exception exception)
        => exception is OperationCanceledException ||
           IsTransportFault(exception) ||
           exception is SharpLinkException
           {
               Code: SharpLinkErrorCode.Unavailable or
                     SharpLinkErrorCode.DeadlineExceeded or
                     SharpLinkErrorCode.HeartbeatTimeout
           };

    private void DispatchHealthResponse(
        ClientConnection connection,
        long requestId,
        ref ReadOnlySequence<byte> payload)
    {
        if (connection.PendingCalls.Dispatch(requestId, ref payload))
            return;

        RecordLateResponse(connection, requestId);
    }

    private sealed class HealthResponseCodec : IRpcCodec<SharpLinkHealthCheckResult>
    {
        internal static HealthResponseCodec Instance { get; } = new();

        public void Serialize(
            in SharpLinkHealthCheckResult value,
            IBufferWriter<byte> buffer)
        {
            if (value.Outcome != SharpLinkHealthProbeOutcome.Success || value.Status is not { } status)
            {
                throw new InvalidOperationException(
                    "Only successful remote health responses can be serialized.");
            }

            ProtocolV2PayloadCodec.WriteHealthResponse(buffer, status);
        }

        public SharpLinkHealthCheckResult Deserialize(in ReadOnlySequence<byte> buffer)
            => ProtocolV2PayloadCodec.ReadHealthResponse(buffer);
    }
}
