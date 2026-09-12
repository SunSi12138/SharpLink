namespace SharpLink.Abstractions;

/// <summary>Describes the lifecycle of the local SharpLink client runtime.</summary>
public enum SharpLinkClientLifecycleState
{
    /// <summary>The client has been built but its local runtime has not started.</summary>
    Created,

    /// <summary>The client is starting its locally owned runtime and supervisors.</summary>
    Starting,

    /// <summary>The local client runtime is running, independently of remote readiness.</summary>
    Running,

    /// <summary>Shutdown has begun and new lifecycle work is being rejected.</summary>
    Draining,

    /// <summary>The client runtime and all framework-owned background work have stopped.</summary>
    Stopped,

    /// <summary>The local client runtime failed irrecoverably.</summary>
    Faulted
}
