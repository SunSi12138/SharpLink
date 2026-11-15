namespace SharpLink.Abstractions;
/// <summary>Dispatches decoded request metadata to a source-generated server contract stub.</summary>
public interface IRpcStub
{
    /// <summary>Resolves complete immutable facts for a known method.</summary>
    bool TryResolveMethod(long methodId, out RpcMethodDescriptor method);

    /// <summary>Gets the stable generated contract identifier.</summary>
    long InterfaceHash { get; }

    /// <summary>Invokes a non-cancellable method that has no response payload.</summary>
    ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args);

    /// <summary>Invokes a cancellable method that has no response payload.</summary>
    ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken);

    /// <summary>Invokes a non-cancellable method and writes its response payload.</summary>
    ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output);

    /// <summary>Invokes a cancellable method and writes its response payload.</summary>
    ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken);
}
