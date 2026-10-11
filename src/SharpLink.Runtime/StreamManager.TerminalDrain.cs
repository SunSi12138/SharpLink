namespace SharpLink.Runtime;

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
        out ValueTask dispatch)
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

    internal bool TryCompleteTerminalStream(long requestId, ushort streamId, Exception? exception)
    {
        var routes = Volatile.Read(ref _dispatchersByRequestId);
        if (routes is null || !routes.TryGetValue(requestId, out var requestRoutes) ||
            !requestRoutes.TryAcquire(streamId, out var entry))
            return false;
        try
        {
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
                    if (!_byStreamId.TryGetValue(streamId, out var current) || !ReferenceEquals(current, expected))
                        return false;
                    _byStreamId.Remove(streamId);
                }
            }
            expected.MarkPeerTerminalReceived();
            expected.Close();
            return true;
        }
    }

}
