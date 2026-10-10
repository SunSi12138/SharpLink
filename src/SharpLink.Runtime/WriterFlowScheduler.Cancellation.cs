namespace SharpLink.Runtime;

internal sealed partial class WriterFlowScheduler
{
    internal void Abort(StreamLease stream, Exception error)
        => AbortCore(stream, error, producerCancellation: false);

    private void AbortCore(StreamLease stream, Exception error, bool producerCancellation)
    {
        ValidateLease(stream);
        ArgumentNullException.ThrowIfNull(error);
        List<PreparedFrame>? discarded = null;
        lock (stream.Gate)
        {
            if (stream.Retired || stream.AbortError is not null || (producerCancellation && stream.Sealed))
                return;
            stream.AbortError = error;
            stream.CleanupInProgress = true;
            Interlocked.Increment(ref _cleanupCount);
            MarkInactiveLocked(stream);
            while (stream.Frames.TryDequeue(out var frame))
                (discarded ??= new()).Add(frame);
            stream.QueuedBytes = 0;
            PulseSpaceLocked(stream);
            PulseDrainLocked(stream);
            NotifyLocked(stream);
        }
        try
        {
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
                    _preparedBudget.Release(frame.BudgetBytes);
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
                if (stream.ProducerBusy)
                {
                    stream.JoinProducerOnExit = true;
                    Interlocked.Increment(ref _pendingStopProducers);
                }
                stream.Retired = true;
                stream.Cancellation.Unregister();
                stream.Cancellation = default;
            }
        }
        lock (_lifecycleGate)
            _streams.Clear();
        _ready.Clear();
        Volatile.Write(ref _writerStopped, true);
        while (_notifications.TryDequeue(out _))
        {
        }
        Interlocked.Decrement(ref _pendingStopProducers);
        TryFinishStopped();
    }
}
