namespace SharpLink.Abstractions;
/// <summary>Dispatches decoded request metadata to a source-generated server contract stub.</summary>
public interface IRpcStub
{
    /// <summary>
    /// Resolves the immutable generated facts for one method. This is the only method-fact table a
    /// server needs per RPC: the returned <see cref="RpcMethodShape"/> carries the invocation kind,
    /// client-stream count, cancellation support and the remaining generated flags in one 32-bit word.
    /// </summary>
    /// <param name="methodHash">The generated method identifier.</param>
    /// <returns>
    /// The generated shape, <see cref="RpcMethodShape.UnknownMethod"/> when the identifier is not part
    /// of this contract, or <see cref="RpcMethodShape.Unresolvable"/> when the stub has no fact table.
    /// </returns>
    RpcMethodShape ResolveMethodShape(long methodHash) => RpcMethodShape.Unresolvable;

    /// <summary>
    /// Projects one already-resolved shape into the public descriptor form. Implementations with a
    /// generated timeout table override this to attach the declared <c>[Timeout]</c>; the default
    /// composes the descriptor without materializing any per-method lookup.
    /// </summary>
    /// <param name="methodHash">The generated method identifier.</param>
    /// <param name="shape">The shape previously returned by <see cref="ResolveMethodShape"/>.</param>
    /// <param name="descriptor">Receives the projected descriptor.</param>
    void DescribeMethod(long methodHash, RpcMethodShape shape, out RpcMethodDescriptor descriptor)
        => descriptor = RpcMethodDescriptor.FromShape(InterfaceHash, methodHash, shape);

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
}
