namespace SharpLink.Abstractions;

/// <summary>Describes immutable server-side context for one RPC invocation.</summary>
public class SharpLinkCallContextSnapshot
{
    private const long NoDeadlineTicks = long.MinValue;
    private readonly long _deadlineTicks;
    private readonly long _deadlineOffsetTicks;

    /// <summary>Creates an immutable server-side call-context snapshot.</summary>
    /// <param name="sessionId">The transport session identifier.</param>
    /// <param name="authentication">The authenticated identity, when present.</param>
    /// <param name="deadline">The negotiated absolute deadline, when present.</param>
    /// <param name="metadata">The immutable request metadata, when present.</param>
    public SharpLinkCallContextSnapshot(
        string sessionId,
        SharpLinkAuthenticationContext? authentication,
        DateTimeOffset? deadline = null,
        SharpLinkMetadata? metadata = null)
    {
        SessionId = sessionId;
        Authentication = authentication;
        if (deadline is { } value)
        {
            _deadlineTicks = value.Ticks;
            _deadlineOffsetTicks = value.Offset.Ticks;
        }
        else
        {
            _deadlineTicks = NoDeadlineTicks;
        }
        Metadata = metadata;
    }

    /// <summary>Gets the transport session identifier.</summary>
    public string SessionId { get; }
    /// <summary>Gets the authenticated identity, when present.</summary>
    public SharpLinkAuthenticationContext? Authentication { get; }
    /// <summary>Gets the negotiated absolute deadline, when present.</summary>
    public DateTimeOffset? Deadline => _deadlineTicks == NoDeadlineTicks
        ? null
        : new DateTimeOffset(
            _deadlineTicks,
            new TimeSpan(_deadlineOffsetTicks));
    /// <summary>Gets immutable request metadata, when present.</summary>
    public SharpLinkMetadata? Metadata { get; }
}
