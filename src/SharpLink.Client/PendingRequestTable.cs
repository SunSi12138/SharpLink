namespace SharpLink.Client;

/// <summary>
/// Stores pending unary operations in a fixed-size, power-of-two table.
/// </summary>
/// <remarks>
/// The common path performs one array lookup and one compare/exchange. A primary-slot
/// collision advances the request ID until an empty primary slot is found, so response
/// dispatch remains O(1). A full table can optionally be awaited without adding a
/// semaphore operation to every successful request.
/// </remarks>
internal sealed class PendingRequestTable : IDisposable
{
    private readonly int _indexMask;
    private readonly IRpcOperation?[] _slots;
    private readonly IRpcCodecProvider _codecProvider;
    private readonly SemaphoreSlim _slotAvailable;
    private long _nextId;
    private int _waiterCount;
    private int _disposed;

    public PendingRequestTable(int capacity = 65_536, IRpcCodecProvider? codecProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (!System.Numerics.BitOperations.IsPow2(capacity))
            throw new ArgumentException("Pending request capacity must be a power of two.", nameof(capacity));

        _slots = new IRpcOperation?[capacity];
        _indexMask = capacity - 1;
        _codecProvider = codecProvider ?? new SharpLinkRuntimeContextBuilder().Build().Codecs;
        _slotAvailable = new SemaphoreSlim(0, capacity);
    }

    public int Capacity => _slots.Length;

    public int Count
    {
        get
        {
            var count = 0;
            for (var index = 0; index < _slots.Length; index++)
                if (Volatile.Read(ref _slots[index]) is not null)
                    count++;
            return count;
        }
    }

    public RpcRequestOperation<T> Rent<T>(out long id)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (TryRent(out id, out RpcRequestOperation<T> operation))
            return operation;

        throw CreateResourceExhaustedException();
    }

    public async ValueTask<PendingRequestLease<T>> RentAsync<T>(
        bool waitForSlot,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (TryRent(out var id, out RpcRequestOperation<T> operation))
            return new PendingRequestLease<T>(id, operation);
        if (!waitForSlot)
            throw CreateResourceExhaustedException();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            Interlocked.Increment(ref _waiterCount);
            try
            {
                // Close the release-before-wait race without charging the normal path.
                if (TryRent(out id, out operation))
                    return new PendingRequestLease<T>(id, operation);

                if (deadline is not { } absoluteDeadline)
                {
                    await _slotAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var remaining = absoluteDeadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        throw CreateDeadlineExceededException();
                    try
                    {
                        await _slotAvailable.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        throw CreateDeadlineExceededException();
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _waiterCount);
            }

            if (TryRent(out id, out operation))
                return new PendingRequestLease<T>(id, operation);
        }
    }

    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)
    {
        if (!TryTakeMatchingOperation(id, out var operation))
            return false;

        operation!.SetResult(ref payload);
        return true;
    }

    public bool DispatchError(long id, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!TryTakeMatchingOperation(id, out var operation))
            return false;

        operation!.SetError(exception);
        return true;
    }

    public long AllocateRequestId()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return NextRequestId();
    }

    public void FailAllPendingRequests(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var index = 0; index < _slots.Length; index++)
        {
            var operation = Interlocked.Exchange(ref _slots[index], null);
            if (operation is null)
                continue;

            ReleaseSlot();
            operation.SetError(exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        FailAllPendingRequests(new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "Pending request table is disposed."));
        _slotAvailable.Dispose();
    }

    private bool TryRent<T>(out long id, out RpcRequestOperation<T> operation)
    {
        operation = RpcOperationPool<T>.Rent();
        id = NextRequestId();
        operation.Initialize(id, _codecProvider);
        var index = (int)(id & _indexMask);
        if (Interlocked.CompareExchange(ref _slots[index], operation, null) is null)
            return true;

        for (var attempt = 1; attempt < _slots.Length; attempt++)
        {
            id = NextRequestId();
            operation.Initialize(id, _codecProvider);
            index = (int)(id & _indexMask);
            if (Interlocked.CompareExchange(ref _slots[index], operation, null) is null)
                return true;
        }

        operation.ReturnError();
        operation = null!;
        id = 0;
        return false;
    }

    private bool TryTakeMatchingOperation(long id, out IRpcOperation? operation)
    {
        var index = (int)(id & _indexMask);
        while (true)
        {
            var current = Volatile.Read(ref _slots[index]);
            if (current is null || current.Id != id)
            {
                operation = null;
                return false;
            }

            var exchanged = Interlocked.CompareExchange(ref _slots[index], null, current);
            if (!ReferenceEquals(exchanged, current))
                continue;

            operation = current;
            ReleaseSlot();
            return true;
        }
    }

    private void ReleaseSlot()
    {
        if (Volatile.Read(ref _waiterCount) == 0)
            return;

        try
        {
            _slotAvailable.Release();
        }
        catch (SemaphoreFullException)
        {
            // A waiter may have completed between the count check and the release.
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    private long NextRequestId()
    {
        var id = Interlocked.Increment(ref _nextId);
        return id != 0 ? id : Interlocked.Increment(ref _nextId);
    }

    private static SharpLinkException CreateResourceExhaustedException()
        => new(SharpLinkErrorCode.ResourceExhausted, "Pending request capacity is exhausted.");

    private static SharpLinkException CreateDeadlineExceededException()
        => new(SharpLinkErrorCode.DeadlineExceeded, "Timed out waiting for pending request capacity.");

    private static class RpcOperationPool<T>
    {
        private const int MaxRetainedOperations = 4096;
        private static readonly ConcurrentStack<RpcRequestOperation<T>> Stack = new();
        private static int _retainedCount;

        public static RpcRequestOperation<T> Rent()
        {
            if (Stack.TryPop(out var operation))
            {
                Interlocked.Decrement(ref _retainedCount);
                return operation;
            }

            return new RpcRequestOperation<T>(Return);
        }

        private static void Return(RpcRequestOperation<T> operation)
        {
            while (true)
            {
                var current = Volatile.Read(ref _retainedCount);
                if (current >= MaxRetainedOperations)
                    return;
                if (Interlocked.CompareExchange(ref _retainedCount, current + 1, current) == current)
                    break;
            }

            Stack.Push(operation);
        }
    }
}

internal readonly record struct PendingRequestLease<T>(long Id, RpcRequestOperation<T> Operation);
