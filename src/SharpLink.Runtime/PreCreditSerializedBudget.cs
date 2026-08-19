namespace SharpLink.Runtime;

/// <summary>
/// Bounds session-owned stream serialization that has not yet acquired protocol flow-control credit.
/// This accounting is intentionally independent from SendPump queued-byte capacity because the two
/// resources have different ownership lifetimes.
/// </summary>
internal sealed class PreCreditSerializedBudget
{
    private readonly Lock _gate = new();
    private readonly long _maxBytes;
    private readonly int _maxWaiters;
    private long _reservedBytes;
    private int _waiterCount;
    private int _contendedAcquires;
    private Waiter? _head;
    private Waiter? _tail;
    private Exception? _terminal;

    internal PreCreditSerializedBudget(long maxBytes, int maxWaiters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWaiters);
        _maxBytes = maxBytes;
        _maxWaiters = maxWaiters;
    }

    internal long MaxBytes => _maxBytes;

    internal long ReservedBytes => Volatile.Read(ref _reservedBytes);

    internal int WaiterCount => Volatile.Read(ref _waiterCount);

    internal ValueTask AcquireAsync(
        long requestId,
        ushort streamId,
        int bytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);
        if (Volatile.Read(ref _terminal) is { } terminal)
            return ValueTask.FromException(terminal);

        if (Volatile.Read(ref _contendedAcquires) == 0 && TryReserveAtomic(bytes))
        {
            if (Volatile.Read(ref _contendedAcquires) == 0)
                return ValueTask.CompletedTask;

            // A contender published itself while the lock-free reservation raced with it.
            // Give the bytes back and let the ordered path decide admission so a late fast-path
            // producer cannot bypass a waiter that is already entering the FIFO.
            ReleaseAtomic(bytes);
            lock (_gate)
                DrainWaiters();
        }

        return AcquireContendedAsync(
            requestId,
            streamId,
            bytes,
            cancellationToken);
    }

    private ValueTask AcquireContendedAsync(
        long requestId,
        ushort streamId,
        int bytes,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _contendedAcquires);
        Waiter waiter;
        lock (_gate)
        {
            if (Volatile.Read(ref _terminal) is { } terminal)
            {
                ExitContendedAcquire();
                return ValueTask.FromException(terminal);
            }

            if (_head is null && TryReserveAtomic(bytes))
            {
                ExitContendedAcquire();
                return ValueTask.CompletedTask;
            }

            if (_waiterCount >= _maxWaiters)
            {
                ExitContendedAcquire();
                return ValueTask.FromException(new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    $"The session already has {_maxWaiters} pre-credit serialized-memory waiters."));
            }

            waiter = new Waiter(requestId, streamId, bytes);
            Enqueue(waiter);
        }

        return new ValueTask(WaitForGrantAsync(waiter, cancellationToken));
    }

    /// <summary>
    /// Replaces a conservative pre-serialization reservation with the exact serialized size.
    /// The caller already owns <paramref name="reservedBytes"/>, so growing above the budget is
    /// legal only when that caller is the sole owner (oversized-item borrow-once semantics).
    /// </summary>
    internal void ResizeReservation(int reservedBytes, int actualBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reservedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actualBytes);

        while (true)
        {
            var current = Volatile.Read(ref _reservedBytes);
            var withoutCurrent = current - reservedBytes;
            if (withoutCurrent < 0)
                throw new InvalidOperationException("Pre-credit serialized byte accounting underflowed.");

            if (actualBytes <= _maxBytes)
            {
                if (withoutCurrent > _maxBytes - actualBytes)
                {
                    throw new InvalidOperationException(
                        "Pre-credit serialized reservation grew beyond the available byte budget.");
                }
            }
            else if (withoutCurrent != 0)
            {
                throw new InvalidOperationException(
                    "An oversized pre-credit stream item must be the sole serialized-byte owner.");
            }

            var updated = checked(withoutCurrent + actualBytes);
            if (Interlocked.CompareExchange(ref _reservedBytes, updated, current) == current)
                break;
        }

        DrainWaitersIfContended();
    }

    internal void Release(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        ReleaseAtomic(bytes);
        DrainWaitersIfContended();
    }

    internal void CompleteStream(
        long requestId,
        ushort streamId,
        Exception? exception = null)
    {
        List<Waiter>? rejected = null;
        lock (_gate)
        {
            var current = _head;
            while (current is not null)
            {
                var next = current.Next;
                if (current.RequestId == requestId && current.StreamId == streamId)
                {
                    Remove(current);
                    current.State = WaiterState.Terminal;
                    (rejected ??= []).Add(current);
                }
                current = next;
            }
            DrainWaiters();
        }

        CompleteRejectedWaiters(
            rejected,
            exception ?? new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "The stream is closed."));
    }

    internal void Complete(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        List<Waiter>? rejected = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _terminal) is not null)
                return;

            Volatile.Write(ref _terminal, exception);
            while (_head is { } waiter)
            {
                Remove(waiter);
                waiter.State = WaiterState.Terminal;
                (rejected ??= []).Add(waiter);
            }
        }

        CompleteRejectedWaiters(rejected, exception);
    }

    private bool TryReserveAtomic(int bytes)
    {
        while (true)
        {
            var current = Volatile.Read(ref _reservedBytes);
            long updated;
            if (bytes <= _maxBytes)
            {
                if (current > _maxBytes - bytes)
                    return false;
                updated = checked(current + bytes);
            }
            else
            {
                if (current != 0)
                    return false;
                updated = bytes;
            }

            if (Interlocked.CompareExchange(ref _reservedBytes, updated, current) == current)
                return true;
        }
    }

    private void ReleaseAtomic(int bytes)
    {
        var remaining = Interlocked.Add(ref _reservedBytes, -bytes);
        if (remaining < 0)
            throw new InvalidOperationException("Pre-credit serialized byte accounting underflowed.");
    }

    private void DrainWaitersIfContended()
    {
        if (Volatile.Read(ref _contendedAcquires) == 0)
            return;
        lock (_gate)
            DrainWaiters();
    }

    private async Task WaitForGrantAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        try
        {
            await waiter.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (waiter.State == WaiterState.Queued)
                {
                    Remove(waiter);
                    waiter.State = WaiterState.Cancelled;
                    DrainWaiters();
                }
                else if (waiter.State == WaiterState.Granted)
                {
                    ReleaseAtomic(waiter.Bytes);
                    waiter.State = WaiterState.Cancelled;
                    DrainWaiters();
                }
            }
            throw;
        }
    }

    private void DrainWaiters()
    {
        while (Volatile.Read(ref _terminal) is null &&
               _head is { } waiter &&
               TryReserveAtomic(waiter.Bytes))
        {
            Remove(waiter);
            waiter.State = WaiterState.Granted;
            waiter.Completion.TrySetResult(true);
        }
    }

    private void Enqueue(Waiter waiter)
    {
        waiter.State = WaiterState.Queued;
        waiter.Previous = _tail;
        if (_tail is null)
            _head = waiter;
        else
            _tail.Next = waiter;
        _tail = waiter;
        _waiterCount++;
    }

    private void Remove(Waiter waiter)
    {
        if (waiter.Previous is null)
            _head = waiter.Next;
        else
            waiter.Previous.Next = waiter.Next;
        if (waiter.Next is null)
            _tail = waiter.Previous;
        else
            waiter.Next.Previous = waiter.Previous;
        waiter.Previous = null;
        waiter.Next = null;
        _waiterCount--;
        if (_waiterCount < 0)
            throw new InvalidOperationException("Pre-credit waiter accounting underflowed.");
        ExitContendedAcquire();
    }

    private void ExitContendedAcquire()
    {
        var remaining = Interlocked.Decrement(ref _contendedAcquires);
        if (remaining < 0)
            throw new InvalidOperationException("Pre-credit contention accounting underflowed.");
    }

    private static void CompleteRejectedWaiters(List<Waiter>? waiters, Exception exception)
    {
        if (waiters is null)
            return;
        for (var index = 0; index < waiters.Count; index++)
            waiters[index].Completion.TrySetException(exception);
    }

    private sealed class Waiter(long requestId, ushort streamId, int bytes)
    {
        internal long RequestId { get; } = requestId;
        internal ushort StreamId { get; } = streamId;
        internal int Bytes { get; } = bytes;
        internal TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal WaiterState State { get; set; }
        internal Waiter? Previous { get; set; }
        internal Waiter? Next { get; set; }
    }

    private enum WaiterState : byte
    {
        Created,
        Queued,
        Granted,
        Cancelled,
        Terminal
    }
}
