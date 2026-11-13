namespace SharpLink.Abstractions;
/// <summary>Dispatches decoded request metadata to a source-generated server contract stub.</summary>
public interface IRpcStub
{
#if SHARPLINK_775_STATIC_SLICE
    /// <summary>Resolves the complete immutable facts; false means the method is unknown.</summary>
    bool TryResolveMethod(long methodId, out RpcMethodDescriptor method);

    /// <summary>Gets the stable generated contract identifier.</summary>
    long InterfaceHash { get; }

    /// <summary>Invokes an already resolved method without a response payload.</summary>
    ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args);
    /// <summary>Invokes an already resolved cancellable method without a response payload.</summary>
    ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken);
    /// <summary>Invokes an already resolved method and writes its response payload.</summary>
    ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output);
    /// <summary>Invokes an already resolved cancellable method and writes its response payload.</summary>
    ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken);
#else
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

    /// <summary>Gets the stable generated contract identifier.</summary>
    long InterfaceHash { get; }

    /// <summary>Invokes a non-cancellable method that has no response payload.</summary>
    ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args);

    /// <summary>Invokes a cancellable method that has no response payload.</summary>
    ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken);

    /// <summary>Invokes a non-cancellable method and writes its response payload.</summary>
    ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output);

    /// <summary>Invokes a cancellable method and writes its response payload.</summary>
    ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken);
#endif
}
