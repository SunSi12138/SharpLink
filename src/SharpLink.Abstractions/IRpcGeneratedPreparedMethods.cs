namespace SharpLink.Abstractions;

/// <summary>Experimental generated server entry table, resolved when preparing a registration.</summary>
public interface IRpcGeneratedPreparedMethods
{
    /// <summary>Gets the exact generated method and its typed execution body.</summary>
    bool TryPrepareMethod(long methodId, out RpcGeneratedPreparedMethod method);
}

/// <summary>Describes one generated entry and the cancellation behavior needed before execution.</summary>
public readonly record struct RpcGeneratedPreparedMethod(
    RpcMethodDescriptor Descriptor, bool SupportsCancellation, RpcGeneratedMethodExecutor Execute);

/// <summary>Runs an already-selected generated body using the registration's bound Codecs.</summary>
public delegate ValueTask RpcGeneratedMethodExecutor(object service, IRpcGeneratedServerBridge bridge,
    long requestId, ReadOnlySequence<byte> arguments, IBufferWriter<byte>? output, CancellationToken cancellationToken);
