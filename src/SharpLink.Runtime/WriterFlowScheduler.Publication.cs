namespace SharpLink.Runtime;

internal sealed partial class WriterFlowScheduler
{
    // Packet ownership transfers on entry, including validation failure.
    internal ValueTask EnqueueAsync(StreamLease stream, IRpcByteBufferWriter packet,
        int creditBytes, CancellationToken token = default)
        => ReserveAndPublishAsync(stream, packet, creditBytes, false, false, token);

    internal ValueTask SealAsync(StreamLease stream, IRpcByteBufferWriter terminalPacket,
        CancellationToken token = default, bool forceFlush = false)
        => ReserveAndPublishAsync(stream, terminalPacket, 0, true, forceFlush, token);

    private async ValueTask ReserveAndPublishAsync(StreamLease stream, IRpcByteBufferWriter packet,
        int creditBytes, bool terminal, bool forceFlush, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var ownsPacket = true;
        Preparation preparation = default;
        try
        {
            ValidatePreparedPacket(packet, creditBytes, terminal);
            preparation = await ReservePreparationAsync(stream, packet.WrittenCount, token).ConfigureAwait(false);
            ownsPacket = false;
            await PublishPreparedAsync(preparation, packet, creditBytes, token, terminal, forceFlush)
                .ConfigureAwait(false);
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
                preparation.Dispose();
            }
        }
    }

    internal async ValueTask PublishPreparedAsync(Preparation preparation, IRpcByteBufferWriter packet,
        int creditBytes, CancellationToken token = default, bool terminal = false, bool forceFlush = false)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var ownsPacket = true;
        try
        {
            if (!ReferenceEquals(preparation.Owner, this) || preparation.Stream is not { } stream)
                throw new InvalidOperationException("Foreign or missing preparation reservation.");
            ValidatePreparedPacket(packet, creditBytes, terminal);
            var bytes = packet.WrittenCount;
            if (bytes > preparation.Bytes)
                throw new InvalidOperationException("Serialized packet exceeds its preparation reservation.");
            while (true)
            {
                // A custom clock runs outside the stream gate, and all terminal
                // state is rechecked afterwards at the publication commit.
                ThrowIfProducerDeadlineExpired(stream);
                Task space;
                lock (stream.Gate)
                {
                    token.ThrowIfCancellationRequested();
                    ThrowIfPublicationRejectedLocked(stream);
                    if (!stream.ProducerBusy || stream.PreparationVersion != preparation.Version ||
                        stream.PreparationReleaseClaimed || stream.ProducerBudgetBytes != preparation.Bytes)
                    {
                        throw new InvalidOperationException("Expired preparation reservation.");
                    }
                    if (CanFitLocked(stream, bytes))
                    {
                        stream.Frames.Enqueue(new PreparedFrame(packet, bytes, preparation.Bytes,
                            creditBytes, terminal, forceFlush));
                        stream.QueuedBytes += bytes;
                        stream.ProducerBudgetBytes = 0;
                        ownsPacket = false;
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
                if (ReferenceEquals(preparation.Owner, this))
                    preparation.Dispose();
            }
        }
    }

    private void ValidatePreparedPacket(IRpcByteBufferWriter packet, int creditBytes, bool terminal)
    {
        var bytes = packet.WrittenCount;
        if (bytes <= 0 || bytes > _maxNormalFrameBytes || packet.WrittenMemory.Length != bytes)
            throw Capacity("Prepared frame cannot fit the normal send-queue allowance.");
        if ((!terminal && (creditBytes <= 0 || creditBytes > _maxCreditBytes)) || (terminal && creditBytes != 0))
            throw Capacity("Invalid encoded stream-item credit size.");
    }

    internal bool TryFindStream(long requestId, ushort streamId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StreamLease? stream)
    {
        lock (_lifecycleGate)
            return _streams.TryGetValue(new StreamKey(requestId, streamId), out stream);
    }

    internal ValueTask WaitForPreparedDrainAsync(StreamLease stream, CancellationToken token,
        bool finishProduction = false)
    {
        ValidateLease(stream);
        lock (stream.Gate)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfStopped();
            if (stream.AbortError is { } abort)
                return ValueTask.FromException(abort);
            if (finishProduction)
                stream.Finishing = true;
            if (stream.Frames.Count == 0 && !stream.ProducerBusy)
                return ValueTask.CompletedTask;
            stream.PreparedDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(stream.PreparedDrained.Task.WaitAsync(token));
        }
    }

    // Called before a normal-queue StreamComplete is committed. The producer
    // first drains prepared DATA into the writer; no transport flush is required.
    internal void PrepareOrdinaryTerminal(StreamLease stream)
    {
        ValidateLease(stream);
        lock (stream.Gate)
        {
            if (stream.Frames.Count != 0 || stream.ProducerBusy)
                throw new InvalidOperationException("Stream terminal requires prepared DATA to drain first.");
            stream.Finishing = true;
        }
    }

    internal void CommitOrdinaryTerminal(StreamLease stream)
    {
        ValidateLease(stream);
        lock (stream.Gate)
        {
            if (stream.Retired)
                return;
            stream.Sealed = true;
            stream.TerminalTaken = true;
            MarkInactiveLocked(stream);
            NotifyLocked(stream);
        }
        _signal();
    }

    private static void PulseDrainLocked(StreamLease stream)
    {
        if (stream.Frames.Count != 0 || stream.ProducerBusy)
            return;
        var drain = stream.PreparedDrained;
        stream.PreparedDrained = null;
        if (stream.AbortError is { } abort)
            drain?.TrySetException(abort);
        else
            drain?.TrySetResult();
    }
}
