namespace SharpLink.Abstractions;

/// <summary>Describes whether the client can currently route calls to its configured remote dependencies.</summary>
public enum SharpLinkReadinessState
{
    /// <summary>No required route currently satisfies its readiness policy.</summary>
    NotReady,

    /// <summary>Some, but not all, required routes satisfy their readiness policy.</summary>
    Degraded,

    /// <summary>All required routes currently satisfy their readiness policy.</summary>
    Ready
}
