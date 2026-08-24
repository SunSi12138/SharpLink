namespace SharpLink.Server;

/// <summary>
/// Binds generated streaming operations to one Server connection. Runtime owns protocol pumping;
/// this invocation-layer bridge owns business exception mapping.
/// </summary>
internal sealed class ServerGeneratedBridge(
    SharpLinkServer server,
    RpcSession session,
    StripedLongMap<ServerCallCancellationState> callCancellations) : IRpcGeneratedServerBridge
{
    private readonly RpcSessionGeneratedServerBridge _protocolBridge = new(session);

    public void EnsureUserCodeEntry(long requestId)
    {
        if (callCancellations.TryCapture(
                requestId,
                static (capturedRequestId, state) => state.CaptureLease(capturedRequestId),
                out var callLease) &&
            callLease.TryAcquire())
        {
            try
            {
                // User-code entry and inbound StreamData acceptance are both progress claims:
                // neither may pass after a strong call terminal, and both promote an already-
                // expired monotonic deadline to the terminal reason before returning.
                if (callLease.State.TryAcceptStreamData())
                    return;
                throw ServerCallTerminationMapper.CreateServerCancellationException(
                    callLease.State.Reason,
                    deadlineExceeded: false);
            }
            finally
            {
                callLease.ReleaseUse();
            }
        }

        // Timed non-cooperative calls intentionally may not allocate a call state. They still
        // carry the frozen receiver deadline in the ambient invocation context.
        if (SharpLinkCallContext.Current is { } context &&
            context.DeadlineTimeProvider is { } timeProvider &&
            context.LocalRpcDeadline.IsExpired(timeProvider))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded.");
        }
    }

    public IAsyncEnumerable<T> CreateInboundStream<T>(
        long requestId,
        ushort streamId,
        IRpcCodec<T> codec,
        bool payloadNullable,
        CancellationToken cancellationToken)
        => _protocolBridge.CreateInboundStream(
            requestId,
            streamId,
            codec,
            payloadNullable,
            cancellationToken);

    public async ValueTask PumpOutboundStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        bool payloadNullable,
        long contractId,
        long methodId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _protocolBridge.PumpOutboundStreamAsync(
                requestId,
                streamId,
                new UserCodeEntryAsyncEnumerable<T>(this, requestId, stream),
                codec,
                payloadNullable,
                contractId,
                methodId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            var protocolError = server.MapStreamServiceException(
                callCancellations,
                session,
                requestId,
                contractId,
                methodId,
                exception);
            session.SendStreamErrorAsync(requestId, streamId, protocolError);
        }
    }

    private sealed class UserCodeEntryAsyncEnumerable<T>(
        ServerGeneratedBridge bridge,
        long requestId,
        IAsyncEnumerable<T> stream) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            bridge.EnsureUserCodeEntry(requestId);
            return new UserCodeEntryAsyncEnumerator(
                bridge,
                requestId,
                stream.GetAsyncEnumerator(cancellationToken));
        }

        private sealed class UserCodeEntryAsyncEnumerator(
            ServerGeneratedBridge bridge,
            long requestId,
            IAsyncEnumerator<T> enumerator) : IAsyncEnumerator<T>
        {
            public T Current => enumerator.Current;

            public ValueTask<bool> MoveNextAsync()
            {
                bridge.EnsureUserCodeEntry(requestId);
                return enumerator.MoveNextAsync();
            }

            public ValueTask DisposeAsync() => enumerator.DisposeAsync();
        }
    }
}
