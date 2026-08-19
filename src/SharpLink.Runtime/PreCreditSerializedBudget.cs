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
    private long _reservedBytes;
    private Waiter? _head;
    private Waiter? _tail;
    private Exception? _terminal;

    internal PreCreditSerializedBudget(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxBytes = maxBytes;
    }

    internal long MaxBytes => _maxBytes;

    internal long ReservedBytes
    {
        get
        {
            lock (_gate)
                return _reservedBytes;
        }
    }

    internal ValueTask AcquireAsync(int bytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);

        Waiter waiter;
        lock (_gate)
        {
            if (_terminal is { } terminal)
                return ValueTask.FromException(terminal);

            if (_head is null && CanReserve(bytes))
            {
                _reservedBytes = checked(_reservedBytes + bytes);
                return ValueTask.CompletedTask;
            }

            waiter = new Waiter(bytes);
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

        lock (_gate)
        {
            var withoutCurrent = _reservedBytes - reservedBytes;
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

            _reservedBytes = checked(withoutCurrent + actualBytes);
            DrainWaiters();
        }
    }

    internal void Release(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_gate)
        {
            _reservedBytes -= bytes;
            if (_reservedBytes < 0)
                throw new InvalidOperationException("Pre-credit serialized byte accounting underflowed.");
            DrainWaiters();
        }
    }

    internal void Complete(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            if (_terminal is not null)
                return;

            _terminal = exception;
            while (_head is { } waiter)
            {
                Remove(waiter);
                waiter.State = WaiterState.Terminal;
                waiter.Completion.TrySetException(exception);
            }
        }
    }

    private bool CanReserve(int bytes)
        => bytes <= _maxBytes
            ? _reservedBytes <= _maxBytes - bytes
            : _reservedBytes == 0;

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
                    _reservedBytes -= waiter.Bytes;
                    if (_reservedBytes < 0)
                    {
                        throw new InvalidOperationException(
                            "Cancelled pre-credit waiter underflowed serialized byte accounting.");
                    }
                    waiter.State = WaiterState.Cancelled;
                    DrainWaiters();
                }
            }
            throw;
        }
    }

    private void DrainWaiters()
    {
        while (_terminal is null && _head is { } waiter && CanReserve(waiter.Bytes))
        {
            Remove(waiter);
            _reservedBytes = checked(_reservedBytes + waiter.Bytes);
            waiter.State = WaiterState.Granted;
            // Continuations are asynchronous, so completing while holding the admission lock cannot
            // re-enter the budget or serialize another item under this lock.
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
    }

    private sealed class Waiter(int bytes)
    {
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
