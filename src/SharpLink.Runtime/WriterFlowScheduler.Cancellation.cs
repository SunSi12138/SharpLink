namespace SharpLink.Runtime;

internal sealed partial class WriterFlowScheduler
{
    // Cancellation may run while transport FlushAsync is blocked. It discards
    // prepared ownership only; admitted frames and credit remain writer-owned.
    internal void Abort(StreamLease stream, Exception error)
    {
        ValidateLease(stream);
        ArgumentNullException.ThrowIfNull(error);
        List<PreparedFrame>? discarded = null;
        lock (stream.Gate)
        {
            if (stream.Retired || stream.AbortError is not null)
                return;
            stream.AbortError = error;
            stream.CleanupInProgress = true;
            Interlocked.Increment(ref _cleanupCount);
            MarkInactiveLocked(stream);
            while (stream.Frames.TryDequeue(out var frame))
                (discarded ??= new()).Add(frame);
            stream.QueuedBytes = 0;
            PulseSpaceLocked(stream);
            NotifyLocked(stream);
        }
        try
        {
            // Do not release the lifecycle key while key-based budget cleanup
            // can still reject a successor's waiter.
            ReleaseDiscarded(discarded);
            _preparedBudget.CompleteStream(stream.RequestId, stream.StreamId, error);
        }
        finally
        {
            lock (stream.Gate)
            {
                stream.CleanupInProgress = false;
                NotifyLocked(stream);
            }
            Interlocked.Decrement(ref _cleanupCount);
            _signal();
            TryFinishStopped();
        }
    }

    internal void AbortRequest(long requestId, Exception error)
    {
        List<StreamLease> matches = new();
        lock (_lifecycleGate)
        {
            foreach (var pair in _streams)
            {
                if (pair.Key.RequestId == requestId)
                    matches.Add(pair.Value);
            }
        }
        foreach (var stream in matches)
            Abort(stream, error);
    }

    internal void StopPreparation(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (Interlocked.CompareExchange(ref _terminal, error, null) is not null)
            return;
        _preparedBudget.Complete(error);
        StreamLease[] streams;
        lock (_lifecycleGate)
        {
            streams = new StreamLease[_streams.Count];
            _streams.Values.CopyTo(streams, 0);
            _openWaiter?.Completion.TrySetException(error);
            _openWaiter = null;
        }
        foreach (var stream in streams)
            Abort(stream, error);
    }

    private void ReleaseDiscarded(List<PreparedFrame>? frames)
    {
        if (frames is null)
            return;
        foreach (var frame in frames)
        {
            try
            {
                _returnPacket(frame.Packet);
            }
            catch (Exception error)
            {
                RecordCleanupFailure(error);
            }
            finally
            {
                try
                {
                    _preparedBudget.Release(frame.SerializedBytes);
                }
                catch (Exception error)
                {
                    RecordCleanupFailure(error);
                }
            }
        }
    }

    public void Stopped(Exception error)
    {
        if (_writerStopped)
            return;
        StopPreparation(error);
        DrainNotifications();
        StreamLease[] remaining;
        lock (_lifecycleGate)
        {
            remaining = new StreamLease[_streams.Count];
            _streams.Values.CopyTo(remaining, 0);
        }
        foreach (var stream in remaining)
        {
            lock (stream.Gate)
            {
                stream.NotificationQueued = false;
                ApplyAbortLocked(stream);
                if (stream.WriterPins != 0)
                    RecordCleanupFailure(new InvalidOperationException("Writer stopped before frame settlement."));
                stream.Retired = true;
                stream.Cancellation.Unregister();
                stream.Cancellation = default;
            }
        }
        // Producers may still be completing canceled waits. The immutable
        // lease remains safe, while durable completion joins their cleanup.
        lock (_lifecycleGate)
            _streams.Clear();
        _ready.Clear();
        Volatile.Write(ref _writerStopped, true);
        while (_notifications.TryDequeue(out _))
        {
        }
        TryFinishStopped();
    }
}
