namespace SharpLink.Abstractions;

/// <summary>Describes the connectivity state of one configured SharpLink cluster runtime.</summary>
public enum SharpLinkClusterState
{
    /// <summary>The cluster runtime has not begun resolving or connecting.</summary>
    Inactive,

    /// <summary>The cluster runtime is resolving its current endpoint topology.</summary>
    Resolving,

    /// <summary>The cluster runtime is establishing an initial usable connection.</summary>
    Connecting,

    /// <summary>The cluster currently has at least one usable connection.</summary>
    Ready,

    /// <summary>The cluster previously had connectivity and is attempting to recover it.</summary>
    Reconnecting,

    /// <summary>The cluster currently has no usable connection.</summary>
    Unavailable,

    /// <summary>The cluster is draining existing work during shutdown or replacement.</summary>
    Draining,

    /// <summary>The cluster runtime has stopped.</summary>
    Stopped
}
