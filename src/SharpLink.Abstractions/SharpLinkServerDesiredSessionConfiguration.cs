namespace SharpLink.Abstractions;

/// <summary>Desired values captured exactly once by each newly accepted server session.</summary>
public sealed record SharpLinkServerDesiredSessionConfiguration
{
    /// <summary>Gets the desired negotiated maximum frame payload for future sessions.</summary>
    public required int MaxFramePayloadBytes { get; init; }
}

/// <summary>Controls whether publication affects only future sessions or also asks capable existing sessions to refresh.</summary>
public enum SharpLinkSessionRolloutMode : byte
{
    /// <summary>Existing sessions remain pinned until they end naturally.</summary>
    FutureOnly = 0,
    /// <summary>Capable existing sessions are asked to replace themselves after publication.</summary>
    RollingRefresh = 1
}

/// <summary>Identifies one immutable desired-session publication owned by one server instance.</summary>
/// <param name="ServerInstanceId">The authority identity within which <paramref name="Generation"/> is comparable.</param>
/// <param name="Generation">The monotonic generation owned by that server instance.</param>
/// <param name="Configuration">The immutable desired values captured by future accepted sessions.</param>
public readonly record struct SharpLinkServerDesiredSessionSnapshot(
    Guid ServerInstanceId,
    ulong Generation,
    SharpLinkServerDesiredSessionConfiguration Configuration);