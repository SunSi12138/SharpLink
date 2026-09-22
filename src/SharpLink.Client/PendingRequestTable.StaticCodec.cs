namespace SharpLink.Client;

internal sealed partial class PendingRequestTable
{
    public RpcRequestOperation<T, TCodec> RentGenerated<T, TCodec>(
        in TCodec responseCodec,
        PendingCallKind kind,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        out long id,
        IPendingCallCompletionObserver? completionObserver = null,
        bool hasResponsePayload = true,
        bool responseNullable = false)
        where TCodec : IRpcCodec<T>
    {
        ArgumentNullException.ThrowIfNull(responseCodec);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (TryRentGenerated<T, TCodec>(
                in responseCodec,
                kind,
                deadline,
                cancellationToken,
                hasResponsePayload,
                responseNullable,
                completionObserver,
                out id,
                out var operation))
        {
            return operation;
        }

        throw CreateResourceExhaustedException();
    }

    private bool TryRentGenerated<T, TCodec>(
        in TCodec responseCodec,
        PendingCallKind kind,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        bool hasResponsePayload,
        bool responseNullable,
        IPendingCallCompletionObserver? completionObserver,
        out long id,
        out RpcRequestOperation<T, TCodec> operation)
        where TCodec : IRpcCodec<T>
    {
        operation = RpcGeneratedOperationPool<T, TCodec>.Rent();
        if (TryRegisterGenerated(
                in responseCodec,
                kind,
                deadline,
                cancellationToken,
                hasResponsePayload,
                responseNullable,
                completionObserver,
                out id,
                operation))
        {
            return true;
        }

        operation.ReturnError();
        operation = null!;
        return false;
    }

    private bool TryRegisterGenerated<T, TCodec>(
        in TCodec responseCodec,
        PendingCallKind kind,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        bool hasResponsePayload,
        bool responseNullable,
        IPendingCallCompletionObserver? completionObserver,
        out long id,
        RpcRequestOperation<T, TCodec> operation)
        where TCodec : IRpcCodec<T>
    {
        if (!TryAcquireCapacity())
        {
            id = 0;
            return false;
        }

        var published = false;
        try
        {
            var slots = GetOrCreateSlots();
            while (true)
            {
                for (var attempt = 0; attempt < slots.Length; attempt++)
                {
                    id = NextRequestId();
                    var index = (int)(id & _indexMask);
                    if (Volatile.Read(ref slots[index]) is not null)
                        continue;

                    operation.Initialize(id, in responseCodec, hasResponsePayload, responseNullable);
                    var call = PendingCall.Rent(
                        this,
                        id,
                        kind,
                        operation,
                        dispatcher: null,
                        deadline,
                        cancellationToken,
                        completionObserver);
                    if (Interlocked.CompareExchange(ref slots[index], call, null) is null)
                    {
                        published = true;
                        OnRegistered(call);
                        CompleteRegistrationIfDisposed(call);
                        return true;
                    }

                    call.ReturnUnused();
                }

                Thread.Yield();
            }
        }
        catch
        {
            if (!published)
                ReleaseCapacity();
            throw;
        }
    }

    private static class RpcGeneratedOperationPool<T, TCodec>
        where TCodec : IRpcCodec<T>
    {
        private const int MaxRetainedOperations = 4096;
        private static readonly ConcurrentQueue<RpcRequestOperation<T, TCodec>> Queue = new();
        private static int _retainedCount;

        public static RpcRequestOperation<T, TCodec> Rent()
        {
            if (Queue.TryDequeue(out var operation))
            {
                Interlocked.Decrement(ref _retainedCount);
                return operation;
            }

            return new RpcRequestOperation<T, TCodec>(Return);
        }

        private static void Return(RpcRequestOperation<T, TCodec> operation)
        {
            while (true)
            {
                var current = Volatile.Read(ref _retainedCount);
                if (current >= MaxRetainedOperations)
                    return;
                if (Interlocked.CompareExchange(ref _retainedCount, current + 1, current) == current)
                    break;
            }

            Queue.Enqueue(operation);
        }
    }
}
