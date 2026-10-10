namespace SharpLink.Runtime;

internal sealed partial class WriterFlowScheduler
{
    // Packet ownership transfers on entry, including validation/cancellation
    // failure. Callers finish codecs and compression before this boundary.
    internal ValueTask EnqueueAsync(StreamLease stream, IRpcByteBufferWriter packet,
        int creditBytes, CancellationToken token = default)
        => PrepareAsync(stream, packet, creditBytes, terminal: false, forceFlush: false, token);

    internal ValueTask SealAsync(StreamLease stream, IRpcByteBufferWriter terminalPacket,
        CancellationToken token = default, bool forceFlush = false)
        => PrepareAsync(stream, terminalPacket, 0, terminal: true, forceFlush, token);

    private async ValueTask PrepareAsync(StreamLease stream, IRpcByteBufferWriter packet,
        int creditBytes, bool terminal, bool forceFlush, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var ownsPacket = true;
        var ownsBudget = false;
        var ownsProducer = false;
        var bytes = 0;
        try
        {
            ValidateLease(stream);
            token.ThrowIfCancellationRequested();
            ThrowIfStopped();
            bytes = packet.WrittenCount;
            if (bytes <= 0 || bytes > _maxNormalFrameBytes || packet.WrittenMemory.Length != bytes)
                throw Capacity("Prepared frame cannot fit the normal send-queue allowance.");
            if ((!terminal && (creditBytes <= 0 || creditBytes > _maxCreditBytes)) ||
                (terminal && creditBytes != 0))
            {
                throw Capacity("Invalid encoded stream-item credit size.");
            }
            lock (stream.Gate)
            {
                ThrowIfPublicationRejectedLocked(stream);
                if (stream.ProducerBusy)
                    throw new InvalidOperationException("A stream supports one active producer.");
                stream.ProducerBusy = true;
                Interlocked.Increment(ref _producerCount);
                ownsProducer = true;
            }
            await _preparedBudget.AcquireAsync(stream.RequestId, stream.StreamId, bytes, token)
                .ConfigureAwait(false);
            ownsBudget = true;
            while (true)
            {
                Task space;
                lock (stream.Gate)
                {
                    token.ThrowIfCancellationRequested();
                    ThrowIfPublicationRejectedLocked(stream);
                    if (CanFitLocked(stream, bytes))
                    {
                        stream.Frames.Enqueue(new PreparedFrame(packet, bytes, creditBytes, terminal, forceFlush));
                        stream.QueuedBytes += bytes;
                        ownsPacket = false;
                        ownsBudget = false;
                        if (terminal)
                        {
                            stream.Sealed = true;
                            MarkInactiveLocked(stream);
                        }
                        NotifyLocked(stream);
                        break;
                    }
                    stream.SpaceChanged ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    space = stream.SpaceChanged.Task;
                }
                await space.WaitAsync(token).ConfigureAwait(false);
            }
            _signal();
        }
        finally
        {
            try
            {
                if (ownsPacket)
                    _returnPacket(packet);
            }
            finally
            {
                try
                {
                    if (ownsBudget)
                        _preparedBudget.Release(bytes);
                }
                finally
                {
                    if (ownsProducer)
                    {
                        var notify = false;
                        lock (stream.Gate)
                        {
                            stream.ProducerBusy = false;
                            notify = stream.Sealed || stream.AbortError is not null;
                            if (notify)
                                NotifyLocked(stream);
                        }
                        Interlocked.Decrement(ref _producerCount);
                        if (notify)
                            _signal();
                        TryFinishStopped();
                    }
                }
            }
        }
    }

    private bool CanFitLocked(StreamLease stream, int bytes)
        => stream.Frames.Count < _slots &&
            (stream.Frames.Count == 0 || bytes <= _perStreamPreparedBytes - stream.QueuedBytes);

    private void ThrowIfPublicationRejectedLocked(StreamLease stream)
    {
        ThrowIfStopped();
        if (stream.AbortError is { } abort)
            throw abort;
        if (stream.Retired || stream.Sealed)
            throw Closed();
    }

    private static void PulseSpaceLocked(StreamLease stream)
    {
        var waiting = stream.SpaceChanged;
        stream.SpaceChanged = null;
        waiting?.TrySetResult();
    }
}
