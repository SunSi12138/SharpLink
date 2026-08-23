namespace SharpLink.Abstractions;
/// <summary>Dispatches decoded request metadata to a source-generated server contract stub.</summary>
public interface IRpcStub
{
    /// <summary>Gets generated metadata for a method when it exists.</summary>
    bool TryGetMethodDescriptor(long methodHash, out RpcMethodDescriptor descriptor)
    {
        descriptor = default;
        return false;
    }

    /// <summary>Gets whether a method consumes a per-call cancellation token or cancellable input stream.</summary>
    /// <param name="methodHash">The generated method identifier.</param>
    /// <returns><see langword="true"/> when the server must create per-call cancellation state.</returns>
    bool SupportsCancellation(long methodHash) => true;

    /// <summary>Binds instance-scoped runtime services before this stub starts serving calls.</summary>
    /// <param name="runtimeContext">The runtime context that owns this stub registration.</param>
    void BindRuntimeContext(IRpcRuntimeContext runtimeContext)
    {
    }

    /// <summary>Gets the stable generated contract identifier.</summary>
    long InterfaceHash { get; }

    /// <summary>Invokes a non-cancellable method that has no response payload.</summary>
    ValueTask InvokeNoReturnAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args);

    /// <summary>Invokes a cancellable method that has no response payload.</summary>
    ValueTask InvokeNoReturnCancellableAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken);

    /// <summary>Invokes a non-cancellable method and writes its response payload.</summary>
    ValueTask InvokeAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output);

    /// <summary>Invokes a cancellable method and writes its response payload.</summary>
    ValueTask InvokeCancellableAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output, CancellationToken cancellationToken);
}
