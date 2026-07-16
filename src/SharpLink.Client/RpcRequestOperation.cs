using System.Threading.Tasks.Sources;

namespace SharpLink.Client;

// 2. 为了在 RingBuffer 数组中存不同 T 的 Operation，需要一个非泛型接口
internal interface IRpcOperation
{
    // 在 IO 线程被调用
    public long Id { get; }
    void SetResult(ref ReadOnlySequence<byte> payload);
    void SetError(Exception ex);
}


internal sealed class RpcRequestOperation<T> : IValueTaskSource<T>, IRpcOperation 
{
    private ManualResetValueTaskSourceCore<T> _core;
    private IRpcCodec<T>? _codec;
    
    private readonly Action<RpcRequestOperation<T>> _returnAction;

    public RpcRequestOperation(Action<RpcRequestOperation<T>> returnAction)
    {
        _returnAction = returnAction;
        _core.RunContinuationsAsynchronously = true;
    }
    
    public long Id { get; private set; }
    public void Initialize(long id, IRpcCodecProvider codecProvider)
    {
        ArgumentNullException.ThrowIfNull(codecProvider);
        Initialize(id, codecProvider.GetCodec<T>());
    }

    public void Initialize(long id, IRpcCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        Id = id;
        _codec = codec;
        _core.Reset(); // 重置状态机
    }
    // 【新增】发送失败时的手动归还
    public void ReturnError() => ReturnToPool();
    
    public T GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            ReturnToPool();
        }
    }
    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);
    
    public void SetResult(ref ReadOnlySequence<byte> payload)
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
            var result = (_codec ?? throw new InvalidOperationException("Request operation has no response codec."))
                .Deserialize(payload);
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

    private void ReturnToPool()
    {
        _codec = null;
        _returnAction(this);
    }

}
