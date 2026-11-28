namespace SharpLink.Abstractions;

/// <summary>Pairs complete method facts with the generated execution body.</summary>
public readonly record struct RpcGeneratedPreparedMethod(
    RpcMethodDescriptor Descriptor, RpcGeneratedMethodExecutor Execute);

/// <summary>Runs an already-selected generated body using the registration's bound Codecs.</summary>
public delegate ValueTask RpcGeneratedMethodExecutor(object service, IRpcGeneratedServerBridge bridge,
    in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> arguments, IBufferWriter<byte>? output,
    CancellationToken cancellationToken);
