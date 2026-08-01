namespace SharpLink.Abstractions;

/// <summary>Describes the aggregate lifecycle state of a multi-cluster client.</summary>
public enum SharpLinkMultiClusterState
{
    /// <summary>The client has been built but has not begun connecting.</summary>
    Created,

    /// <summary>All required cluster slots are being connected.</summary>
    Connecting,

    /// <summary>Every configured cluster slot is ready.</summary>
    Ready,

    /// <summary>At least one slot is unavailable after a successful initial connection.</summary>
    Degraded,

    /// <summary>Stop has begun and new dynamic registrations are rejected.</summary>
    Draining,

    /// <summary>All owned cluster slots have stopped.</summary>
    Stopped,

    /// <summary>An initial connection or coordinator operation failed irrecoverably.</summary>
    Faulted
}
