namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <inheritdoc />
    public async ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = GetReadyConnection();
        var session = connection.Session;
        if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.HealthCheck) == 0)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.Unimplemented,
                "The server did not negotiate protocol health checks.");
        }

        var deadline = _hasRequestTimeout
            ? AddTimeout(DateTimeOffset.UtcNow, _requestTimeoutValue)
            : (DateTimeOffset?)null;
        var operation = connection.PendingCalls.Rent(
            HealthResponseCodec.Instance,
            PendingCallKind.Health,
            GetMonotonicDeadlineTimestamp(deadline, DateTimeOffset.UtcNow),
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
            => ProtocolV2PayloadCodec.WriteHealthResponse(buffer, value.Status);

        public SharpLinkHealthCheckResult Deserialize(in ReadOnlySequence<byte> buffer)
            => ProtocolV2PayloadCodec.ReadHealthResponse(buffer);
    }
}
