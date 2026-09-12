namespace SharpLink.Abstractions;

/// <summary>Describes whether a SharpLink server can currently accept new RPC calls.</summary>
public enum SharpLinkHealthStatus : byte
{
    /// <summary>The process is stopped, faulted, or has not completed startup.</summary>
    Unhealthy = 0,

    /// <summary>The listener is running and the server can accept new calls.</summary>
    Ready = 1,

    /// <summary>The server is rejecting new calls while existing calls drain.</summary>
    Draining = 2
}

/// <summary>Describes whether a protocol health probe obtained a remote health response.</summary>
public enum SharpLinkHealthProbeOutcome : byte
{
    /// <summary>No Ready connection was available when the local probe started.</summary>
    NotReady = 0,

    /// <summary>The peer returned a valid protocol health response.</summary>
    Success = 1,

    /// <summary>The probe started but could not complete because the selected connection became unavailable.</summary>
    Unavailable = 2,

    /// <summary>The selected peer did not negotiate the protocol health-check capability.</summary>
    Unsupported = 3
}

/// <summary>
/// Represents one health probe result, separating local reachability/capability from a remote health response.
/// </summary>
/// <remarks>
/// <see cref="Status"/> is populated only when <see cref="Outcome"/> is
/// <see cref="SharpLinkHealthProbeOutcome.Success"/>. Expected local NotReady, connection loss, and peer
/// capability absence are represented by <see cref="Outcome"/> instead of exception control flow.
/// </remarks>
public readonly record struct SharpLinkHealthCheckResult
{
    /// <summary>Creates a successful probe result from one protocol-level remote health response.</summary>
    /// <param name="status">The remote process readiness state.</param>
    public SharpLinkHealthCheckResult(SharpLinkHealthStatus status)
    {
        Outcome = SharpLinkHealthProbeOutcome.Success;
        Status = status;
    }

    private SharpLinkHealthCheckResult(SharpLinkHealthProbeOutcome outcome)
    {
        Outcome = outcome;
        Status = null;
    }

    /// <summary>Gets the stable local probe outcome.</summary>
    public SharpLinkHealthProbeOutcome Outcome { get; }

    /// <summary>
    /// Gets the remote readiness state when <see cref="Outcome"/> is
    /// <see cref="SharpLinkHealthProbeOutcome.Success"/>.
    /// </summary>
    public SharpLinkHealthStatus? Status { get; }

    /// <summary>Gets a result indicating that no Ready connection was available for the probe.</summary>
    public static SharpLinkHealthCheckResult NotReady { get; } =
        new(SharpLinkHealthProbeOutcome.NotReady);

    /// <summary>Gets a result indicating that an in-flight probe lost reachability.</summary>
    public static SharpLinkHealthCheckResult Unavailable { get; } =
        new(SharpLinkHealthProbeOutcome.Unavailable);

    /// <summary>Gets a result indicating that the selected peer does not support protocol health checks.</summary>
    public static SharpLinkHealthCheckResult Unsupported { get; } =
        new(SharpLinkHealthProbeOutcome.Unsupported);
}
