namespace SharpLink.Client;

internal class RequestManager
{
    private readonly int _indexMask;
    private readonly IRpcOperation?[] _slots;
    private readonly IRpcCodecProvider _codecProvider;
    
    // 全局自增 ID
    private long _nextId;

    public RequestManager(int capacity = 65_536, IRpcCodecProvider? codecProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (!System.Numerics.BitOperations.IsPow2(capacity))
            throw new ArgumentException("Request capacity must be a power of two.", nameof(capacity));

        _slots = new IRpcOperation?[capacity];
        _indexMask = capacity - 1;
        _codecProvider = codecProvider ?? new SharpLinkRuntimeContextBuilder().Build().Codecs;
    }
    
    public RpcRequestOperation<T> Rent<T>(out long id)
    {
        // 1. 生成 ID
        id = Interlocked.Increment(ref _nextId);
        
        // 2. 从静态池租借对象 (复用内存)
        var op = RpcOperationPool<T>.Rent();
        op.Initialize(id, _codecProvider);

        // 3. 注册到 Ring Buffer
        var index = (int)(id & _indexMask);
        
        // 乐观锁注册
        var original = Interlocked.CompareExchange(ref _slots[index], op, null);
        if (original == null)
            return op;
        
        // 极其罕见的 RingBuffer 耗尽，归还对象并报错
        op.ReturnError();
        throw new SharpLinkException(
            SharpLinkErrorCode.ResourceExhausted,
            $"Pending request capacity is exhausted at slot {index}.");

    }

    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)
    {
        if (!TryTakeMatchingOperation(id, out var op)) return false;
        op!.SetResult(ref payload);
        return true;

    }
    
    // 如果有 Error 处理需求类似 Dispatch
    public bool DispatchError(long id, Exception ex)
    {
        if (!TryTakeMatchingOperation(id, out var op)) return false;
        op!.SetError(ex);
        return true;

    }

    public long AllocateRequestId() => Interlocked.Increment(ref _nextId);

    private void FailAll(Exception ex)
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            var op = Interlocked.Exchange(ref _slots[i], null);
            op?.SetError(ex);
        }
    }

    public void FailAllPendingRequests(Exception ex) => FailAll(ex);

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
            if (!ReferenceEquals(exchanged, current)) continue;
            operation = current;
            return true;
        }
    }

    private static class RpcOperationPool<T>
    {
        private const int MaxRetainedOperations = 4096;
        // 使用 ConcurrentStack 或者简单的无锁链表实现池
        private static readonly ConcurrentStack<RpcRequestOperation<T>> Stack = new();
        private static int _retainedCount;

        public static RpcRequestOperation<T> Rent()
        {
            if (Stack.TryPop(out var op))
            {
                Interlocked.Decrement(ref _retainedCount);
                return op;
            }

            return new RpcRequestOperation<T>(Return);
        }

        private static void Return(RpcRequestOperation<T> op)
        {
            while (true)
            {
                var current = Volatile.Read(ref _retainedCount);
                if (current >= MaxRetainedOperations)
                    return;
                if (Interlocked.CompareExchange(ref _retainedCount, current + 1, current) == current)
                    break;
            }
            Stack.Push(op);
        }
    }
}
