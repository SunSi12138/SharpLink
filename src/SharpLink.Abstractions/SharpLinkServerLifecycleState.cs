namespace SharpLink.Abstractions;

/// <summary>Describes the lifecycle of the local SharpLink server runtime independently of process lifetime.</summary>
public enum SharpLinkServerLifecycleState : byte
{
    /// <summary>The server object exists but startup has not begun.</summary>
    Created = 0,

    /// <summary>Local runtime and serving infrastructure are starting.</summary>
    Starting = 1,

    /// <summary>The local serving surface is active and can accept connections.</summary>
    Running = 2,

    /// <summary>Shutdown has begun and new work is no longer admitted.</summary>
    Draining = 3,

    /// <summary>Shutdown and owned resource cleanup completed normally.</summary>
    Stopped = 4,

    /// <summary>An unrecoverable local runtime or shutdown failure terminated the server.</summary>
    Faulted = 5
}
