namespace SharpLink.Runtime;

// Numeric identity avoids retaining a pooled call state (and its next invocation).
internal readonly record struct StreamTerminalOwner(long StateId, long Generation)
{
    internal bool IsPresent => StateId != 0;
}

internal sealed partial class StreamManager
{
    /// <summary>
    /// Accounts a terminal call's transport data only on an existing stable inbound route.
    /// This never creates a route or enters an attached typed consumer.
    /// </summary>
    internal bool TryDiscardTerminalStreamData(
        long requestId,
        ushort streamId,
        ReadOnlySequence<byte> payload,
        int encodedByteCount,
        out ValueTask dispatch,
        StreamTerminalOwner terminalOwner = default)
    {
        dispatch = default;
        var routes = Volatile.Read(ref _dispatchersByRequestId);
        if (routes is null || !routes.TryGetValue(requestId, out var requestRoutes) ||
            !requestRoutes.TryAcquire(streamId, out var entry))
            return false;
        try
        {
            ThrowIfPeerTerminal(entry);
            var dispatcher = entry.Dispatcher;
            if (dispatcher is not DiscardingStreamDispatcher &&
                dispatcher is not PreAdmissionStreamDispatcher { IsDiscarding: true })
            {
                if (dispatcher is PreAdmissionStreamDispatcher retained && retained.IsRetainedInputOf(terminalOwner))
                {
                    // Cancel can precede a final queued chunk. The retained route owns
                    // the debit and refund, but must not enter its original typed child.
                    _acceptBytes?.Invoke(requestId, streamId, encodedByteCount);
                    _bytesConsumed?.Invoke(requestId, streamId, encodedByteCount);
                    entry.Release();
                    return true;
                }
                entry.Release();
                return false;
            }

            // Confirm discard before debit; the same leased route refunds this frame exactly
            // once. A compressed frame uses its validated original length, without decompression.
            _acceptBytes?.Invoke(requestId, streamId, encodedByteCount);
            dispatch = CompleteDispatch(entry,
                ((IStreamConsumptionAwareDispatcher)dispatcher).DispatchAsync(payload, encodedByteCount));
            return true;
        }
        catch
        {
            entry.Release();
            throw;
        }
    }

    internal bool TryCompleteTerminalStream(long requestId, ushort streamId, Exception? exception,
        StreamTerminalOwner terminalOwner = default)
    {
        var routes = Volatile.Read(ref _dispatchersByRequestId);
        if (routes is null || !routes.TryGetValue(requestId, out var requestRoutes) ||
            !requestRoutes.TryAcquire(streamId, out var entry))
            return false;
        try
        {
            if (entry.Dispatcher is PreAdmissionStreamDispatcher retained && retained.IsRetainedInputOf(terminalOwner))
            {
                // EOF may precede the handler/queued admission's physical unwind.
                // Record the peer terminal without entering or releasing its child.
                entry.MarkPeerTerminalReceived();
                if (!CompleteCapturedTerminalStream(requestId, streamId, requestRoutes, entry, exception))
                    PublishReceiveTerminal(requestId, streamId, entry);
                return true;
            }
            return CompleteCapturedTerminalStream(requestId, streamId, requestRoutes, entry, exception);
        }
        finally
        {
            entry.Release();
        }
    }

    private bool CompleteCapturedTerminalStream(long requestId, ushort streamId,
        RequestDispatchers requestRoutes, DispatcherEntry entry, Exception? exception)
    {
        if (entry.Dispatcher is not DiscardingStreamDispatcher &&
            entry.Dispatcher is not PreAdmissionStreamDispatcher { IsDiscarding: true })
            return false;
        if (!requestRoutes.TryRemoveTerminal(streamId, entry))
            return false;
        SharpLinkTelemetry.AddActiveStreams(-1);
        Interlocked.Decrement(ref _activeStreamCount);
        try
        {
            entry.Dispatcher.Complete(exception);
            PublishReceiveTerminal(requestId, streamId, entry);
        }
        finally
        {
            entry.Detach();
            RemoveEmptyRequest(requestId, requestRoutes);
        }
        return true;
    }

    private sealed partial class RequestDispatchers
    {
        internal bool TryRemoveTerminal(ushort streamId, DispatcherEntry expected)
        {
            if (streamId == 0)
            {
                if (!ReferenceEquals(Interlocked.CompareExchange(ref _defaultDispatcher, null, expected), expected))
                    return false;
            }
            else
            {
                lock (_gate)
                {
                    var named = _byStreamId;
                    if (named is null || !named.TryGetValue(streamId, out var current) ||
                        !ReferenceEquals(current, expected))
                        return false;
                    named.Remove(streamId);
                }
            }
            expected.MarkPeerTerminalReceived();
            expected.Close();
            return true;
        }
    }

}
