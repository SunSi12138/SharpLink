using System.Threading.Tasks.Sources;

namespace SharpLink.Client;

internal sealed class RpcRequestOperation<T, TCodec> : IValueTaskSource<T>, IRpcOperation
    where TCodec : IRpcCodec<T>
{
    private ManualResetValueTaskSourceCore<T> _core;
    private TCodec _codec = default!;
    private T? _response;
    private bool _hasResponsePayload;
    private bool _responseNullable;

    private readonly Action<RpcRequestOperation<T, TCodec>> _returnAction;

    public RpcRequestOperation(Action<RpcRequestOperation<T, TCodec>> returnAction)
    {
        _returnAction = returnAction;
        _core.RunContinuationsAsynchronously = true;
    }

    public long Id { get; private set; }

    public void Initialize(
        long id,
        in TCodec codec,
        bool hasResponsePayload = true,
        bool responseNullable = false)
    {
        ArgumentNullException.ThrowIfNull(codec);
        Id = id;
        _codec = codec;
        _hasResponsePayload = hasResponsePayload;
        _responseNullable = responseNullable;
    }

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

    public void OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
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

            _response = _codec.Deserialize(payload);
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
        if (exception is null)
            _core.SetResult(_response!);
        else
            _core.SetException(exception);
    }

    public void SetError(Exception ex) => _core.SetException(ex);

    public ValueTask<T> AsValueTask() => new(this, _core.Version);

    private void ReturnToPool()
    {
        _codec = default!;
        _response = default;
        _hasResponsePayload = false;
        _responseNullable = false;
        _core.Reset();
        _returnAction(this);
    }
}
