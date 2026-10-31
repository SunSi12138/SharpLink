namespace SharpLink.Abstractions;
/// <summary>Dispatches decoded request metadata to a source-generated server contract stub.</summary>
public interface IRpcStub
{
    /// <summary>Resolves all immutable method facts once, including cancellation and the timeout value.</summary>
    /// <param name="methodHash">The generated method identifier.</param>
    /// <returns>The complete known facts, or <see cref="RpcMethodFacts.UnknownMethod"/>.</returns>
    RpcMethodFacts ResolveMethodFacts(long methodHash);

    /// <summary>Gets the stable generated contract identifier.</summary>
    long InterfaceHash { get; }

    /// <summary>Invokes a non-cancellable method that has no response payload.</summary>
    ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args);

    /// <summary>Invokes a cancellable method that has no response payload.</summary>
    ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken);

    /// <summary>Invokes a non-cancellable method and writes its response payload.</summary>
    ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output);

    /// <summary>Invokes a cancellable method and writes its response payload.</summary>
    ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken);
}
