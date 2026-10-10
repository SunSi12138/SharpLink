namespace SharpLink.Runtime;

internal sealed partial class WriterFlowScheduler
{
    private sealed class OpenWaiter(long requestId, ushort streamId, bool startAllowed)
    {
        internal readonly long RequestId = requestId;
        internal readonly ushort StreamId = streamId;
        internal readonly bool StartAllowed = startAllowed;
        internal readonly TaskCompletionSource<StreamLease> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal ValueTask<StreamLease> OpenAsync(long requestId, ushort streamId,
        CancellationToken cancellationToken = default, bool startAllowed = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StreamLease? stream = null;
        OpenWaiter? waiter = null;
        lock (_lifecycleGate)
        {
            ThrowIfStopped();
            if (_streams.ContainsKey(new StreamKey(requestId, streamId)))
                throw Closed();
            if (_openWaiter is not null)
                throw Capacity("The connection already has a pending stream-capacity admission.");
            if (_streams.Count < _maxStreams)
                stream = CreateStreamLocked(requestId, streamId, startAllowed);
            else if (Volatile.Read(ref _activeStreams) >= _maxStreams)
                throw Capacity("The connection reached its active stream limit.");
            else
                waiter = _openWaiter = new OpenWaiter(requestId, streamId, startAllowed);
        }
        if (stream is not null)
        {
            BindCancellation(stream, cancellationToken);
            return new ValueTask<StreamLease>(stream);
        }
        return AwaitOpenAsync(waiter!, cancellationToken);
    }

    private StreamLease CreateStreamLocked(long requestId, ushort streamId, bool startAllowed)
    {
        var generation = checked(++_generation);
        var stream = new StreamLease(this, requestId, streamId, generation, startAllowed);
        _streams.Add(new StreamKey(requestId, streamId), stream);
        Interlocked.Increment(ref _activeStreams);
        return stream;
    }

    private async ValueTask<StreamLease> AwaitOpenAsync(OpenWaiter waiter, CancellationToken token)
    {
        using var registration = token.UnsafeRegister(_ => CancelOpen(waiter, token), null);
        var stream = await waiter.Completion.Task.ConfigureAwait(false);
        BindCancellation(stream, token);
        return stream;
    }

    private void CancelOpen(OpenWaiter waiter, CancellationToken token)
    {
        lock (_lifecycleGate)
        {
            if (!ReferenceEquals(_openWaiter, waiter))
                return;
            _openWaiter = null;
            waiter.Completion.TrySetCanceled(token);
        }
    }

    private void BindCancellation(StreamLease stream, CancellationToken token)
    {
        if (!token.CanBeCanceled)
            return;
        var registration = token.UnsafeRegister(_ => Abort(stream, new OperationCanceledException(token)), null);
        var unregister = false;
        lock (stream.Gate)
        {
            unregister = stream.Retired;
            if (!unregister)
                stream.Cancellation = registration;
        }
        // Synchronous cancellation can retire the identity before registration
        // returns. Do not leave a late registration rooted in that case.
        if (unregister)
            registration.Unregister();
    }

    internal void AllowStart(StreamLease stream)
    {
        ValidateLease(stream);
        lock (stream.Gate)
        {
            if (stream.Retired || stream.AbortError is not null)
                return;
            stream.StartAllowed = true;
            NotifyLocked(stream);
        }
        _signal();
    }

    internal void PostWindowUpdate(long requestId, ushort streamId, int creditBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(creditBytes);
        StreamLease? stream;
        lock (_lifecycleGate)
        {
            ThrowIfStopped();
            _streams.TryGetValue(new StreamKey(requestId, streamId), out stream);
        }
        if (stream is null)
            return;
        lock (stream.Gate)
        {
            if (stream.Retired || stream.AbortApplied)
                return;
            var cap = Math.Max(_streamWindow, _maxCreditBytes);
            stream.PendingReturn = Math.Min(cap, stream.PendingReturn + creditBytes);
            NotifyLocked(stream);
        }
        _signal();
    }

    // Writer-only, stream gate held. The immutable lease is never a reused
    // slot; cleanup also pins the key until all key-based budget work finishes.
    private void TryRetireLocked(StreamLease stream)
    {
        if (stream.Retired || (!stream.TerminalTaken && !stream.AbortApplied) ||
            stream.Credit != _streamWindow || stream.WriterPins != 0 ||
            stream.Frames.Count != 0 || stream.ProducerBusy || stream.CleanupInProgress ||
            stream.NotificationQueued)
        {
            return;
        }
        RemoveReadyLocked(stream);
        stream.Retired = true;
        MarkInactiveLocked(stream);
        stream.Cancellation.Unregister();
        stream.Cancellation = default;
        lock (_lifecycleGate)
        {
            _streams.Remove(new StreamKey(stream.RequestId, stream.StreamId));
            if (_openWaiter is { } waiter && Volatile.Read(ref _terminal) is null)
            {
                _openWaiter = null;
                try
                {
                    waiter.Completion.TrySetResult(
                        CreateStreamLocked(waiter.RequestId, waiter.StreamId, waiter.StartAllowed));
                }
                catch (Exception error)
                {
                    waiter.Completion.TrySetException(error);
                }
            }
        }
    }
}
