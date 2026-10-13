namespace SharpLink.Server;

/// <summary>Retains the admitted stream identity without constructing interceptor state.</summary>
internal sealed class StreamCallContextSnapshot(
    string sessionId,
    SharpLinkAuthenticationContext? authentication,
    RpcDeadline deadline,
    TimeProvider timeProvider,
    SharpLinkMetadata? metadata,
    RpcMethodDescriptor method,
    long requestId,
    CancellationToken cancellationToken)
    : SharpLinkCallContextSnapshot(sessionId, authentication, deadline, timeProvider, metadata)
{
    internal RpcMethodDescriptor Method { get; } = method;
    internal long RequestId { get; } = requestId;
    internal CancellationToken CancellationToken { get; } = cancellationToken;
}
