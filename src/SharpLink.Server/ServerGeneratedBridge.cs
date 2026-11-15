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
        => new UserCodeEntryAsyncEnumerable<T>(
            this,
            requestId,
            _protocolBridge.CreateInboundStream(
                requestId,
                streamId,
                codec,
                payloadNullable,
                cancellationToken));

    public ValueTask PumpOutboundStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        RpcMethodDescriptor method,
        CancellationToken cancellationToken)
        => new OutputInvocation<T>(this, requestId, stream).PumpAsync(
            streamId, codec, method, cancellationToken);

    private void CompleteFailedOutput(
        long requestId, ushort streamId, RpcMethodDescriptor method, Exception exception)
    {
        // A selected call terminal already owns the externally visible outcome. Close
        // only local send flow here; the invocation still owns its physical output join.
        if (GetSelectedTerminal(requestId) is { } selectedTerminal)
        {
            session.CompleteSendStream(requestId, streamId, selectedTerminal);
            return;
        }

        var protocolError = server.MapStreamServiceException(
            callCancellations, session, requestId, method, exception);
        session.SendStreamErrorAsync(requestId, streamId, protocolError);
    }

    private SharpLinkException? GetSelectedTerminal(long requestId)
    {
        if (callCancellations.TryCapture(
                requestId,
                static (capturedRequestId, state) => state.CaptureLease(capturedRequestId),
                out var callLease) &&
            callLease.TryAcquire())
        {
            try
            {
                var reason = callLease.State.Reason;
                if (reason is not (ServerCallCancellationReason.None or ServerCallCancellationReason.Completed))
                {
                    return ServerCallTerminationMapper.CreateServerCancellationException(
                        reason,
                        deadlineExceeded: reason == ServerCallCancellationReason.DeadlineExceeded);
                }
            }
            finally
            {
                callLease.ReleaseUse();
            }
        }

        if (SharpLinkCallContext.Current is { } context &&
            context.DeadlineTimeProvider is { } timeProvider &&
            context.LocalRpcDeadline.IsExpired(timeProvider))
        {
            return new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded.");
        }
        return null;
    }

    private sealed class UserCodeEntryAsyncEnumerable<T>(
        ServerGeneratedBridge bridge, long requestId, IAsyncEnumerable<T> stream) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            bridge.EnsureUserCodeEntry(requestId);
            return new UserCodeEntryAsyncEnumerator<T>(bridge, requestId, stream.GetAsyncEnumerator(cancellationToken));
        }
    }

    private sealed class OutputInvocation<T>(
        ServerGeneratedBridge bridge, long requestId, IAsyncEnumerable<T> stream)
        : IAsyncEnumerable<T>, IGeneratedOutputPhysicalLifetime
    {
        // Replaces the existing output-wrapper stream reference only on abandon.
        private object _streamOrTail = stream;

        public GeneratedOutputPhysicalTail GetPhysicalTail()
        {
            if (_streamOrTail is GeneratedOutputPhysicalTail existing)
                return existing;
            var tail = new GeneratedOutputPhysicalTail();
            _streamOrTail = tail;
            return tail;
        }

        private ValueTask JoinPhysicalOutputAsync()
            => _streamOrTail is GeneratedOutputPhysicalTail tail ? tail.JoinAsync() : ValueTask.CompletedTask;

        internal async ValueTask PumpAsync(
            ushort streamId, IRpcCodec<T> codec, RpcMethodDescriptor method, CancellationToken cancellationToken)
        {
            try
            {
                await bridge._protocolBridge.PumpOutboundStreamAsync(
                    requestId, streamId, this, codec, method, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                bridge.CompleteFailedOutput(requestId, streamId, method, exception);
            }
            finally
            {
                // Existing invocation ownership encloses this physical join, retaining
                // the exact service/module lease, scope and original call-state CTS.
                await JoinPhysicalOutputAsync().ConfigureAwait(false);
            }
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            bridge.EnsureUserCodeEntry(requestId);
            return new UserCodeEntryAsyncEnumerator<T>(bridge, requestId,
                ((IAsyncEnumerable<T>)_streamOrTail).GetAsyncEnumerator(cancellationToken));
        }
    }

    private sealed class UserCodeEntryAsyncEnumerator<T>(
        ServerGeneratedBridge bridge, long requestId, IAsyncEnumerator<T> enumerator) : IAsyncEnumerator<T>
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
