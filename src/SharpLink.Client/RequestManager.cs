namespace SharpLink.Client;

internal class RequestManager
{
    private const int BufferSize = 65536; // 必须是 2 的幂
    private const int IndexMask = BufferSize - 1;
    
    private readonly IRpcOperation?[] _slots = new IRpcOperation?[BufferSize];
    
    // 全局自增 ID
    private long _nextId;
    
    public RpcRequestOperation<T> Rent<T>(out long id)
    {
        // 1. 生成 ID
        id = Interlocked.Increment(ref _nextId);
        
        // 2. 从静态池租借对象 (复用内存)
        var op = RpcOperationPool<T>.Rent();
        op.Initialize(id);

        // 3. 注册到 Ring Buffer
        var index = (int)(id & IndexMask);
        
        // 乐观锁注册
        var original = Interlocked.CompareExchange(ref _slots[index], op, null);
        if (original == null)
            return op;
        
        // 极其罕见的 RingBuffer 耗尽，归还对象并报错
        op.ReturnError();
        throw new InvalidOperationException($"Request RingBuffer exhausted at index {index}!");

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
        var index = (int)(id & IndexMask);

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
        // 使用 ConcurrentStack 或者简单的无锁链表实现池
        private static readonly ConcurrentStack<RpcRequestOperation<T>> Stack = new();

        public static RpcRequestOperation<T> Rent()
        {
            return Stack.TryPop(out var op) ? op :
                // 新建对象时，传入归还委托
                new RpcRequestOperation<T>(Return);
        }

        private static void Return(RpcRequestOperation<T> op)
        {
            Stack.Push(op);
        }
    }
}