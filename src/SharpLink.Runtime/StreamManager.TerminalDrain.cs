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
            if (dispatcher is PreAdmissionStreamDispatcher preAdmission)
                preAdmission.Abandon(out _);
            else if (dispatcher is not DiscardingStreamDispatcher)
            {
                entry.Release();
                return false;
            }

            // Establish discard before debit; the same leased route refunds this frame exactly
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
            if (entry.Dispatcher is PreAdmissionStreamDispatcher preAdmission)
                preAdmission.Abandon(out _);
            else if (entry.Dispatcher is not DiscardingStreamDispatcher)
                return false;
            CompletePeerStream(requestId, streamId, exception);
            return true;
        }
        finally
        {
            entry.Release();
        }
    }
}
