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

/// <summary>Represents one protocol-level server health response.</summary>
/// <param name="Status">The remote process readiness state.</param>
public readonly record struct SharpLinkHealthCheckResult(SharpLinkHealthStatus Status);
