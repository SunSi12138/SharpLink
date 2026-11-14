namespace SharpLink.Abstractions;
/// <summary>Dispatches decoded request metadata to a source-generated server contract stub.</summary>
public interface IRpcStub
{
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
}
