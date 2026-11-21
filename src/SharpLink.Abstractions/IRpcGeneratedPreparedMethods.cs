namespace SharpLink.Abstractions;

/// <summary>Experimental generated server entry table, resolved when preparing a registration.</summary>
public interface IRpcGeneratedPreparedMethods
{
    /// <summary>Gets the exact generated method and its typed execution body.</summary>
    bool TryPrepareMethod(long methodId, out RpcGeneratedPreparedMethod method);
}

/// <summary>Pairs complete method facts with the generated execution body.</summary>
public readonly record struct RpcGeneratedPreparedMethod(
    RpcMethodDescriptor Descriptor, RpcGeneratedMethodExecutor Execute);

/// <summary>Runs an already-selected generated body using the registration's bound Codecs.</summary>
public delegate ValueTask RpcGeneratedMethodExecutor(object service, IRpcGeneratedServerBridge bridge,
    in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> arguments, IBufferWriter<byte>? output,
    CancellationToken cancellationToken);
