namespace SharpLink.Abstractions;
/// <summary>
/// 服务端Stub抽象
/// </summary>
public interface IRpcStub
{
    /// <summary>Gets whether a method consumes a per-call cancellation token or cancellable input stream.</summary>
    /// <param name="methodHash">The generated method identifier.</param>
    /// <returns><see langword="true"/> when the server must create per-call cancellation state.</returns>
    bool SupportsCancellation(long methodHash) => true;

    long InterfaceHash { get; }
    ValueTask InvokeNoReturnAsync(object service,IRpcSession session, long methodHash,long requestId, ReadOnlySequence<byte> args);
    ValueTask InvokeNoReturnCancellableAsync(object service,IRpcSession session, long methodHash,long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken);
    ValueTask InvokeAsync(object service,IRpcSession session, long methodHash,long requestId, ReadOnlySequence<byte> args,IRpcByteBufferWriter output);
    ValueTask InvokeCancellableAsync(object service,IRpcSession session, long methodHash,long requestId, ReadOnlySequence<byte> args,IRpcByteBufferWriter output, CancellationToken cancellationToken);
}
