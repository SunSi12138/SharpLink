namespace SharpLink.Abstractions;

/// <summary>Describes immutable server-side context for one RPC invocation.</summary>
/// <param name="sessionId">The transport session identifier.</param>
/// <param name="authentication">The authenticated identity, when present.</param>
/// <param name="deadline">The negotiated absolute deadline, when present.</param>
/// <param name="metadata">The immutable request metadata, when present.</param>
public sealed class SharpLinkCallContextSnapshot(
    string sessionId,
    SharpLinkAuthenticationContext? authentication,
    DateTimeOffset? deadline = null,
    SharpLinkMetadata? metadata = null)
{
    /// <summary>Gets the transport session identifier.</summary>
    public string SessionId { get; } = sessionId;
    /// <summary>Gets the authenticated identity, when present.</summary>
    public SharpLinkAuthenticationContext? Authentication { get; } = authentication;
    /// <summary>Gets the negotiated absolute deadline, when present.</summary>
    public DateTimeOffset? Deadline { get; } = deadline;
    /// <summary>Gets immutable request metadata, when present.</summary>
    public SharpLinkMetadata? Metadata { get; } = metadata;
}
