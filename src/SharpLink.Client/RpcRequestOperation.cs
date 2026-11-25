using System.Threading.Tasks.Sources;

namespace SharpLink.Client;

// 2. 为了在 RingBuffer 数组中存不同 T 的 Operation，需要一个非泛型接口
internal interface IRpcOperation
{
    // 在 IO 线程被调用
    public long Id { get; }
    Exception? TryDeserializeResponse(ref ReadOnlySequence<byte> payload);
    void CompleteResponse(Exception? exception);
    void SetError(Exception ex);
}


internal sealed class RpcRequestOperation<T> : IValueTaskSource<T>, IRpcOperation
{
    private ManualResetValueTaskSourceCore<T> _core;
    private IRpcCodec<T>? _codec;
    private T? _response;
    private bool _hasResponsePayload;
    private bool _responseNullable;
    private SharpLinkClient? _logicalOwner;

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

    public void Initialize(
        long id,
        IRpcCodec<T> codec,
        bool hasResponsePayload = true,
        bool responseNullable = false)
    {
        ArgumentNullException.ThrowIfNull(codec);
        Id = id;
        _codec = codec;
        _hasResponsePayload = hasResponsePayload;
        _responseNullable = responseNullable;
    }
    // 【新增】发送失败时的手动归还
    public void ReturnError() => ReturnToPool();

    public T GetResult(short token)
    {
        // Validate before taking responsibility for the operation's return to the pool.
        if (_core.GetStatus(token) == ValueTaskSourceStatus.Pending)
            throw new InvalidOperationException("The RPC request has not completed.");
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

    public Exception? TryDeserializeResponse(ref ReadOnlySequence<byte> payload)
    {
        try
        {
            if (!_hasResponsePayload)
            {
                if (!payload.IsEmpty)
                {
                    return new SharpLinkException(
                        SharpLinkErrorCode.DataLoss,
                        "A payload-less RPC response contains unexpected bytes.");
                }
                _response = default;
                return null;
            }
            // 【IO线程反序列化】
            // 此时 payload 有效，直接转为 T 对象，不拷贝 bytes
            _response = (_codec ?? throw new InvalidOperationException("Request operation has no response codec."))
                .Deserialize(payload);
            if (!_responseNullable && default(T) is null && _response is null)
            {
                return new SharpLinkException(
                    SharpLinkErrorCode.DataLoss,
                    "A non-nullable RPC response was null.");
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    public void CompleteResponse(Exception? exception)
    {
        var response = _response;
        ReleaseCompletionOwners();
        if (exception is null)
            _core.SetResult(response!);
        else
            _core.SetException(exception);
    }

    public void SetError(Exception ex)
    {
        ReleaseCompletionOwners();
        _core.SetException(ex);
    }
    public ValueTask<T> AsValueTask() => new(this, _core.Version);

    // Set before the pending table publishes the operation. Only its terminal winner
    // can clear the owner; the application cannot consume the result before that.
    internal void SetLogicalOwner(SharpLinkClient? owner) => _logicalOwner = owner;

    private void ReleaseCompletionOwners()
    {
        var owner = _logicalOwner;
        if (owner is null)
            return;
        // Results may remain unobserved; retain the result, not its original codec or client.
        _codec = null;
        _response = default;
        _logicalOwner = null;
        owner.ReleaseDirectLogicalInvocation();
    }

    private void ReturnToPool()
    {
        _codec = null;
        _response = default;
        _hasResponsePayload = false;
        _responseNullable = false;
        _logicalOwner = null;
        // Clear the continuation and its state before this operation enters the
        // process-wide generic pool. A continuation can close over request types
        // from a collectible AssemblyLoadContext even when T itself is static.
        _core.Reset();
        _returnAction(this);
    }

}
