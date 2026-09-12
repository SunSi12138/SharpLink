namespace SharpLink.Abstractions;

/// <summary>Describes immutable server-side context for one RPC invocation.</summary>
public class SharpLinkCallContextSnapshot
{
    /// <summary>Creates an immutable server-side call-context snapshot.</summary>
    /// <param name="sessionId">The transport session identifier.</param>
    /// <param name="authentication">The authenticated identity, when present.</param>
    /// <param name="metadata">The immutable request metadata, when present.</param>
    public SharpLinkCallContextSnapshot(
        string sessionId,
        SharpLinkAuthenticationContext? authentication,
        SharpLinkMetadata? metadata = null)
    {
        SessionId = sessionId;
        Authentication = authentication;
        Metadata = metadata;
    }

    internal SharpLinkCallContextSnapshot(
        string sessionId,
        SharpLinkAuthenticationContext? authentication,
        RpcDeadline deadline,
        TimeProvider deadlineTimeProvider,
        SharpLinkMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(deadlineTimeProvider);
        SessionId = sessionId;
        Authentication = authentication;
        LocalRpcDeadline = deadline;
        DeadlineTimeProvider = deadlineTimeProvider;
        Metadata = metadata;
    }

    internal RpcDeadline LocalRpcDeadline { get; }
    internal TimeProvider? DeadlineTimeProvider { get; }

    /// <summary>Gets the transport session identifier.</summary>
    public string SessionId { get; }
    /// <summary>Gets the authenticated identity, when present.</summary>
    public SharpLinkAuthenticationContext? Authentication { get; }
    /// <summary>Gets immutable request metadata, when present.</summary>
    public SharpLinkMetadata? Metadata { get; }
}
