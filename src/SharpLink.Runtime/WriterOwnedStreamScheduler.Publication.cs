namespace SharpLink.Runtime;

internal sealed partial class WriterOwnedStreamScheduler
{
    // Ownership transfers on entry, including validation/cancellation failure. Serialization and
    // compression have already completed; neither may run through this writer's callbacks.
    internal async ValueTask EnqueueAsync(
        StreamHandle handle,
        IRpcByteBufferWriter packet,
        int creditBytes,
        bool terminal = false,
        bool forceFlush = false,
        CancellationToken cancellationToken = default,
        RpcDeadline deadline = default,
        TimeProvider? deadlineTimeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var ownsPacket = true;
        var ownsBudget = false;
        var ownsProducer = false;
        var bytes = 0;
        try
        {
            ValidateOwner(handle);
            bytes = packet.WrittenCount;
            if (bytes <= 0 || bytes > _maxPacketBytes || packet.WrittenMemory.Length != bytes ||
                (terminal ? creditBytes != 0 : creditBytes <= 0 || creditBytes > _maxCreditBytes))
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "Invalid or oversized prepared stream frame.");
            lock (handle.Gate)
            {
                ValidateOpen(handle);
                if (handle.ProducerActive != 0)
                    throw new InvalidOperationException("A stream can have only one active frame producer.");
                handle.ProducerActive = ProducerUseMask;
                ownsProducer = true;
            }
            // This call already owns a serialized packet. Charge it before a full stream
            // slot can suspend the producer; otherwise each active stream could retain an
            // unaccounted packet outside both the byte limit and the bounded budget waiters.
            // The writer never needs this preparation budget to dequeue an older packet.
            await AcquirePreparedBudgetAsync(handle, bytes, cancellationToken).ConfigureAwait(false);
            ownsBudget = true;
            while (true)
            {
                Task? wait = null;
                lock (handle.Gate)
                {
                    ValidateOpen(handle);
                    if (handle.Frames.Count >= _slots)
                    {
                        handle.SpaceChanged ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                        wait = handle.SpaceChanged.Task;
                    }
                }
                if (wait is null) break;
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            // TimeProvider is an extension boundary. Never call it under a stream gate.
            if (deadlineTimeProvider is not null && deadline.IsExpired(deadlineTimeProvider))
                throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "RPC deadline exceeded before stream publication.");
            cancellationToken.ThrowIfCancellationRequested();
            var notify = false;
            lock (handle.Gate)
            {
                ValidateOpen(handle);
                cancellationToken.ThrowIfCancellationRequested();
                handle.Frames.Enqueue(new PreparedFrame(packet, bytes, creditBytes, terminal, forceFlush));
                handle.AcceptedFrames++;
                if (terminal) Volatile.Write(ref handle.EndQueued, true);
                ownsPacket = false;
                ownsBudget = false;
                if (!handle.Scheduled) { handle.Scheduled = true; notify = true; }
            }
            if (notify) Notify(handle);
        }
        finally
        {
            try
            {
                if (ownsPacket) _returnBuffer(packet);
            }
            finally
            {
                try { if (ownsBudget) _prepared.Release(bytes); }
                finally
                {
                    if (ownsProducer)
                    {
                        // Abort cleanup may still hold upper units in this existing use word.
                        // Releasing the producer must not clear those independent ownerships.
                        lock (handle.Gate) handle.ProducerActive &= ~ProducerUseMask;
                        if (Volatile.Read(ref handle.EndQueued) || Volatile.Read(ref handle.Error) is not null) Notify(handle);
                    }
                }
            }
        }
    }

    // Called while Enqueue owns the producer use. A late Abort can run after the last open
    // check but before the budget waiter is registered. Register first, then recheck Error:
    // either Abort sees this waiter, or this recheck supplies the otherwise lost rejection.
    internal ValueTask AcquirePreparedBudgetAsync(StreamHandle handle, int bytes, CancellationToken token)
    {
        Debug.Assert((handle.ProducerActive & ProducerUseMask) != 0);
        var pending = _prepared.AcquireAsync(handle.RequestId, handle.StreamId, bytes, token);
        if (!pending.IsCompletedSuccessfully && Volatile.Read(ref handle.Error) is { } error)
            _prepared.CompleteStream(handle.RequestId, handle.StreamId, error);
        return pending;
    }

    internal void PostWindowUpdate(long requestId, ushort streamId, int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        StreamHandle? stream;
        lock (_mapGate)
        {
            ThrowIfClosing();
            _streams.TryGetValue(new StreamKey(requestId, streamId), out stream);
        }
        if (stream is null) return;
        lock (stream.Gate)
        {
            if (!stream.Attached) return;
            stream.PendingCredit = checked(stream.PendingCredit + bytes);
        }
        Notify(stream);
    }

    private void Notify(StreamHandle stream)
    {
        if (Interlocked.Exchange(ref stream.ChangeQueued, 1) == 0) _changes.Enqueue(stream);
        _signal();
    }
}
