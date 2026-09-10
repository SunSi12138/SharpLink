namespace SharpLink.Abstractions;

/// <summary>
/// Describes the legacy aggregate coordinator connection/readiness projection.
/// Use <see cref="SharpLinkClientLifecycleState"/> and <see cref="SharpLinkReadinessState"/>
/// when lifecycle and remote readiness must be distinguished.
/// </summary>
public enum SharpLinkMultiClusterState
{
    /// <summary>The coordinator has been built but has not begun connecting.</summary>
    Created,

    /// <summary>Required cluster slots are being connected.</summary>
    Connecting,

    /// <summary>Every configured cluster slot is ready.</summary>
    Ready,

    /// <summary>At least one configured cluster slot is currently unavailable.</summary>
    Degraded,

    /// <summary>Stop has begun and new dynamic registrations are rejected.</summary>
    Draining,

    /// <summary>All owned cluster slots have stopped.</summary>
    Stopped,

    /// <summary>A legacy initial connection or coordinator operation failed irrecoverably.</summary>
    Faulted
}
