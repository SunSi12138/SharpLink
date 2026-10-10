namespace SharpLink.Runtime;

internal sealed partial class WriterFlowScheduler
{
    internal readonly struct Preparation : IDisposable
    {
        internal readonly WriterFlowScheduler? Owner;
        internal readonly StreamLease? Stream;
        internal readonly long Version;
        internal readonly int Bytes;

        internal Preparation(WriterFlowScheduler owner, StreamLease stream, long version, int bytes)
        {
            Owner = owner;
            Stream = stream;
            Version = version;
            Bytes = bytes;
        }

        public void Dispose() => Owner?.ReleasePreparation(this);
    }

    // Exact-size codecs acquire this local memory reservation BEFORE they
    // allocate or serialize. No protocol credit is taken on this producer.
    internal async ValueTask<Preparation> ReservePreparationAsync(StreamLease stream, int bytes,
        CancellationToken token = default)
    {
        ValidateLease(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        token.ThrowIfCancellationRequested();
        ThrowIfProducerDeadlineExpired(stream);
        long version;
        lock (stream.Gate)
        {
            ThrowIfPublicationRejectedLocked(stream);
            if (stream.ProducerBusy)
                throw new InvalidOperationException("A stream supports one active producer.");
            version = checked(stream.PreparationVersion + 1);
            stream.PreparationVersion = version;
            stream.PreparationReleaseClaimed = false;
            stream.ProducerBusy = true;
        }
        var ownsBudget = false;
        var returned = false;
        try
        {
            await _preparedBudget.AcquireAsync(stream.RequestId, stream.StreamId, bytes, token)
                .ConfigureAwait(false);
            ownsBudget = true;
            ThrowIfProducerDeadlineExpired(stream);
            lock (stream.Gate)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfPublicationRejectedLocked(stream);
                stream.ProducerBudgetBytes = bytes;
                returned = true;
                ownsBudget = false;
            }
            return new Preparation(this, stream, version, bytes);
        }
        finally
        {
            if (!returned)
            {
                try
                {
                    if (ownsBudget)
                        _preparedBudget.Release(bytes);
                }
                finally
                {
                    ExitProducer(stream, version);
                }
            }
        }
    }

    private void ReleasePreparation(Preparation preparation)
    {
        var stream = preparation.Stream!;
        int bytes;
        lock (stream.Gate)
        {
            if (!stream.ProducerBusy || stream.PreparationVersion != preparation.Version ||
                stream.PreparationReleaseClaimed)
                return;
            stream.PreparationReleaseClaimed = true;
            bytes = stream.ProducerBudgetBytes;
            stream.ProducerBudgetBytes = 0;
        }
        try
        {
            if (bytes != 0)
                _preparedBudget.Release(bytes);
        }
        finally
        {
            ExitProducer(stream, preparation.Version);
        }
    }

    private void ExitProducer(StreamLease stream, long version)
    {
        bool notify;
        bool joined;
        lock (stream.Gate)
        {
            if (!stream.ProducerBusy || stream.PreparationVersion != version)
                return;
            stream.ProducerBusy = false;
            joined = stream.JoinProducerOnExit;
            stream.JoinProducerOnExit = false;
            notify = stream.Sealed || stream.AbortError is not null;
            if (notify)
                NotifyLocked(stream);
            PulseDrainLocked(stream);
        }
        // No connection-wide producer counter on the ordinary item path.
        // Shutdown enrolls only the producers that were still active at its cut.
        if (joined)
        {
            Interlocked.Decrement(ref _pendingStopProducers);
            TryFinishStopped();
        }
        if (notify)
            _signal();
    }

    private static void ThrowIfProducerDeadlineExpired(StreamLease stream)
    {
        if (stream.DeadlineClock is { } clock && stream.Deadline.IsExpired(clock))
        {
            throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded,
                "RPC deadline exceeded before stream publication.");
        }
    }

    private void ThrowIfPublicationRejectedLocked(StreamLease stream)
    {
        ThrowIfStopped();
        if (stream.AbortError is { } abort)
            throw abort;
        if (stream.Retired || stream.Sealed || stream.Finishing)
            throw Closed();
    }

    private bool CanFitLocked(StreamLease stream, int bytes)
        => stream.Frames.Count < _slots &&
            (stream.Frames.Count == 0 || bytes <= _perStreamPreparedBytes - stream.QueuedBytes);

    private static void PulseSpaceLocked(StreamLease stream)
    {
        var waiting = stream.SpaceChanged;
        stream.SpaceChanged = null;
        waiting?.TrySetResult();
    }
}
