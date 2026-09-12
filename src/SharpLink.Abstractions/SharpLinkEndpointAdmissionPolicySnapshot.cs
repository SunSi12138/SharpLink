namespace SharpLink.Abstractions;

/// <summary>Identifies the currently published client endpoint-admission mode.</summary>
public enum SharpLinkEndpointAdmissionPolicyKind : byte
{
    /// <summary>No endpoint admission policy is active.</summary>
    Disabled,

    /// <summary>An application-owned <see cref="ISharpLinkEndpointAdmissionPolicy"/> is active.</summary>
    Custom,

    /// <summary>The built-in endpoint-generation circuit breaker is active.</summary>
    CircuitBreaker
}

/// <summary>Describes the current atomic endpoint-admission publication.</summary>
/// <param name="Generation">The monotonically increasing runtime publication generation.</param>
/// <param name="Kind">The currently active admission mode.</param>
public readonly record struct SharpLinkEndpointAdmissionPolicySnapshot(
    ulong Generation,
    SharpLinkEndpointAdmissionPolicyKind Kind);
