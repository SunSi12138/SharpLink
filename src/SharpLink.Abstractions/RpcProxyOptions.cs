namespace SharpLink.Abstractions;

/// <summary>Specifies how an invocation behaves when no compatible endpoint is currently observed.</summary>
public enum RpcAvailability
{
    /// <summary>Fails before sending application data when no compatible endpoint is available.</summary>
    FailFast = 0,

    /// <summary>Waits for compatibility within the invocation's cancellation and deadline budget.</summary>
    WaitForCompatible = 1
}

/// <summary>Configures immutable per-proxy contract admission behavior.</summary>
public sealed record RpcProxyOptions
{
    /// <summary>Gets the invocation availability policy; the default fails fast.</summary>
    public RpcAvailability Availability { get; init; }

    /// <summary>Gets an optional receipt-based requirement that must agree with the proxy's local wire identity.</summary>
    public ContractRequirement? Requirement { get; init; }
}

/// <summary>Configures an explicit point-in-time contract-readiness wait.</summary>
public sealed record ContractWaitOptions
{
    /// <summary>Gets the minimum number of distinct compatible endpoints to observe; the default is one.</summary>
    public int MinimumCompatibleEndpoints { get; init; } = 1;

    /// <summary>Gets an optional positive timeout for this wait, independent of other callers.</summary>
    public TimeSpan? Timeout { get; init; }
}
