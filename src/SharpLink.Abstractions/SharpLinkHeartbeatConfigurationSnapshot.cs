namespace SharpLink.Abstractions;

/// <summary>Describes the currently published client heartbeat scheduling and liveness configuration.</summary>
/// <param name="Generation">The monotonically increasing runtime publication generation.</param>
/// <param name="Interval">The delay between heartbeat Ping scheduling points.</param>
/// <param name="Timeout">The maximum retained peer inactivity before the connection is closed.</param>
public readonly record struct SharpLinkHeartbeatConfigurationSnapshot(
    ulong Generation,
    TimeSpan Interval,
    TimeSpan Timeout);
