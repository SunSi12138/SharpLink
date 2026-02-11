using System.Threading;
using System.Threading.Tasks.Sources;

namespace SharpLink.Client;

// 2. 为了在 RingBuffer 数组中存不同 T 的 Operation，需要一个非泛型接口
internal interface IRpcOperation
{
    // 在 IO 线程被调用
    public long Id { get; }
    void SetResult(ref ReadOnlySequence<byte> payload, ISerializer serializer);
    void SetError(Exception ex);
}

// 伪代码概念
internal sealed class RpcRequestOperation<T> : IValueTaskSource<T>, IRpcOperation 
{
    private ManualResetValueTaskSourceCore<T> _core;
    
    private readonly Action<RpcRequestOperation<T>> _returnAction;

    public RpcRequestOperation(Action<RpcRequestOperation<T>> returnAction)
    {
        _returnAction = returnAction;
        _core.RunContinuationsAsynchronously = true;
    }
    
    public long Id { get; private set; }
    public void Initialize(long id)
    {
        Id = id;
        _core.Reset(); // 重置状态机
    }
    // 【新增】发送失败时的手动归还
    public void ReturnError() => _returnAction(this);
    
    public T GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            _returnAction(this);
        }
    }
    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);
    
    public void SetResult(ref ReadOnlySequence<byte> payload, ISerializer serializer)
    {
        try
        {
            if (payload.Length == 0)
            {
                _core.SetResult(default!);
                return;
            }
            // 【IO线程反序列化】
            // 此时 payload 有效，直接转为 T 对象，不拷贝 bytes
            var result = serializer.Deserialize<T>(ref payload);
            _core.SetResult(result!);
        }
        catch (Exception ex)
        {
            _core.SetException(ex);
        }
    }
    public void SetError(Exception ex)
    {
        _core.SetException(ex);
    }
    public ValueTask<T> AsValueTask() => new(this, _core.Version);
}



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

    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload, ISerializer serializer)
    {
        var index = (int)(id & IndexMask);
        
        // 取出操作并置空插槽 (原子操作)
        var op = Interlocked.Exchange(ref _slots[index], null);
        
        if (op is not null && op.Id == id)
        {
            // 可以在这里校验 op.Id == id 防止错位（虽然理论上不会）
            op.SetResult(ref payload, serializer);
            return true;
        }

        return false;
    }
    
    // 如果有 Error 处理需求类似 Dispatch
    public bool DispatchError(long id, Exception ex)
    {
        var index = (int)(id & IndexMask);
        var op = Interlocked.Exchange(ref _slots[index], null);
        if (op is not null && op.Id == id)
        {
            op.SetError(ex);
            return true;
        }

        return false;
    }

    public long AllocateRequestId() => Interlocked.Increment(ref _nextId);

    public void FailAll(Exception ex)
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            var op = Interlocked.Exchange(ref _slots[i], null);
            if (op is not null)
            {
                op.SetError(ex);
            }
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
