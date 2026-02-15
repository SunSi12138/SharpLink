namespace SharpLink.Abstractions;
/// <summary>
/// 服务端Stub抽象
/// </summary>
public interface IRpcStub
{
    long InterfaceHash { get; }
    ValueTask InvokeNoReturnAsync(object service,IRpcSession session, long methodHash,long requestId, ReadOnlySequence<byte> args);
    ValueTask InvokeNoReturnCancellableAsync(object service,IRpcSession session, long methodHash,long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken);
    ValueTask InvokeAsync(object service,IRpcSession session, long methodHash,long requestId, ReadOnlySequence<byte> args,IBufferWriter<byte> output);
    ValueTask InvokeCancellableAsync(object service,IRpcSession session, long methodHash,long requestId, ReadOnlySequence<byte> args,IBufferWriter<byte> output, CancellationToken cancellationToken);
}
