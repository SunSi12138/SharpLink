namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <inheritdoc />
    public async ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = GetReadySession();
        if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.HealthCheck) == 0)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.Unimplemented,
                "The server did not negotiate protocol health checks.");
        }

        var operation = _requestManager.Rent(HealthResponseCodec.Instance, out var requestId);
        var deadline = _hasRequestTimeout
            ? DateTimeOffset.UtcNow + _requestTimeoutValue
            : (DateTimeOffset?)null;
        using var timeoutRegistration = RegisterRequestTimeout(
            deadline,
            requestId,
            isOneWay: false);
        await using var cancelRegistration = RegisterCancel(
            cancellationToken,
            requestId,
            isOneWay: false,
            cancellationToken);
        try
        {
            BindRequestToSession(requestId, session);
            session.SendHealthCheck(requestId);
        }
        catch (Exception exception)
        {
            TryUnbindRequest(requestId, out _);
            _requestManager.DispatchError(requestId, exception);
        }

        return await operation.AsValueTask().ConfigureAwait(false);
    }

    private void DispatchHealthResponse(long requestId, ref ReadOnlySequence<byte> payload)
    {
        if (_requestManager.Dispatch(requestId, ref payload))
        {
            TryUnbindRequest(requestId, out _);
            return;
        }
        if (_locallyCanceledRequestIds.Remove(requestId))
            return;

        using var requestScope = BeginRequestLogScope(_logger, requestId);
        LogUnknownOrTimedOutResponse(_logger);
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
