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
                EnsureUserCodeEntry(callLease.State);
                return;
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

    private static void EnsureUserCodeEntry(ServerCallCancellationState callState)
    {
        if (callState.TryAcceptStreamData())
            return;
        throw ServerCallTerminationMapper.CreateServerCancellationException(
            callState.Reason,
            deadlineExceeded: false);
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
        // This framework-owned pump, unlike an application-owned inbound enumerator, joins
        // physical producer/disposal cleanup before returning. Pin its call generation once
        // rather than reacquiring the same map entry on every user-code progress boundary.
        var ownsCallLease = callCancellations.TryCapture(
                requestId,
                static (capturedRequestId, state) => state.CaptureLease(capturedRequestId),
                out var callLease) && callLease.TryAcquire();
        try
        {
            await _protocolBridge.PumpOutboundStreamAsync(
                requestId,
                streamId,
                ownsCallLease
                    ? new OutboundUserCodeEntryAsyncEnumerable<T>(callLease.State, stream)
                    : new UserCodeEntryAsyncEnumerable<T>(this, requestId, stream),
                codec,
                payloadNullable,
                contractId,
                methodId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            try
            {
                // Once the framework call owner has selected a terminal, this pump has lost both
                // user-code and wire-publication ownership. Do not run the application exception
                // mapper and do not publish a second StreamComplete(Error); the selected call terminal
                // is already responsible for the externally visible outcome. The local send-flow state
                // still belongs to this pump and must be retired even though no wire terminal is sent.
                if (GetSelectedTerminal(requestId, out var publishModuleDrainResponse) is { } selectedTerminal)
                {
                    session.CompleteSendStream(requestId, streamId, selectedTerminal);
                    if (publishModuleDrainResponse)
                    {
                        await session.SendRpcErrorWithBackpressureAsync(
                            requestId, selectedTerminal, session.LifetimeToken).ConfigureAwait(false);
                    }
                    return;
                }

                var protocolError = server.MapStreamServiceException(
                    callCancellations,
                    session,
                    requestId,
                    contractId,
                    methodId,
                    SharpLinkPhysicalWork.GetLogicalException(exception));
                session.SendStreamErrorAsync(requestId, streamId, protocolError);
            }
            finally
            {
                // Publish the logical terminal first, but keep the existing invocation, service
                // and module owners until abandoned producer/send/disposal work physically ends.
                if (SharpLinkPhysicalWork.GetAbandonedUserWork(exception) is { } physicalWork)
                    await physicalWork.ConfigureAwait(false);
            }
        }
        finally
        {
            // Includes the exceptional physical-work join above: neither a selected public
            // terminal nor removal from the lookup map permits this generation to be recycled.
            if (ownsCallLease)
                callLease.ReleaseUse();
        }
    }

    private SharpLinkException? GetSelectedTerminal(long requestId, out bool publishModuleDrainResponse)
    {
        publishModuleDrainResponse = false;
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
                    // The outer dispatcher normally sends this response after invocation ends.
                    // Physical cleanup can outlive that terminal, so claim it here before awaiting
                    // cleanup; the same claim prevents a second response from the dispatcher.
                    publishModuleDrainResponse = reason == ServerCallCancellationReason.ModuleDraining &&
                        callLease.State.TryClaimModuleDrainResponse();
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

    private sealed class OutboundUserCodeEntryAsyncEnumerable<T>(
        ServerCallCancellationState callState,
        IAsyncEnumerable<T> stream) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            EnsureUserCodeEntry(callState);
            return new Enumerator(callState, stream.GetAsyncEnumerator(cancellationToken));
        }

        private sealed class Enumerator(
            ServerCallCancellationState callState,
            IAsyncEnumerator<T> enumerator) : IAsyncEnumerator<T>
        {
            public T Current => enumerator.Current;

            public ValueTask<bool> MoveNextAsync()
            {
                EnsureUserCodeEntry(callState);
                return enumerator.MoveNextAsync();
            }

            public ValueTask DisposeAsync() => enumerator.DisposeAsync();
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
