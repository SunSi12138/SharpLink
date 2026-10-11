namespace SharpLink.Abstractions;

/// <summary>Describes the observed availability of a contract without promising future admission.</summary>
public enum ContractObservationState
{
    /// <summary>A callable session has not yet supplied its contract manifest.</summary>
    ManifestNotObserved = 0,

    /// <summary>A current manifest does not advertise the required contract.</summary>
    Absent = 1,

    /// <summary>A current session does not satisfy the wire identity or registration requirement.</summary>
    ObservedIncompatible = 2,

    /// <summary>At least one current session satisfies the complete contract requirement.</summary>
    ObservedCompatible = 3,

    /// <summary>The active topology has no callable session for the observed endpoint.</summary>
    EndpointUnavailable = 4,

    /// <summary>The target client or local contract registration has stopped accepting calls.</summary>
    TargetStopped = 5
}

/// <summary>Contains scalar facts from one current endpoint session, or an unavailable endpoint.</summary>
/// <param name="EndpointId">The configured endpoint identity.</param>
/// <param name="SessionId">The observed session identity, or an empty string when no session is callable.</param>
/// <param name="EndpointGeneration">The local endpoint generation.</param>
/// <param name="ManifestGeneration">The observed manifest generation, or null before manifest observation.</param>
/// <param name="State">The result of applying the requirement to this observation.</param>
/// <param name="RpcAssemblyHash">The advertised contract hash, or null when it has not been advertised.</param>
/// <param name="ServerInstanceId">The observed server identity, or an empty GUID when unavailable.</param>
/// <param name="RegistryEpoch">The observed registry timeline identity, or an empty GUID when unavailable.</param>
/// <param name="RegistrationId">The advertised registration identity, or an empty GUID when unavailable.</param>
public sealed record ContractEndpointObservation(
    string EndpointId,
    string SessionId,
    long EndpointGeneration,
    long? ManifestGeneration,
    ContractObservationState State,
    RpcHash128? RpcAssemblyHash,
    Guid ServerInstanceId = default,
    Guid RegistryEpoch = default,
    Guid RegistrationId = default);

/// <summary>An immutable point-in-time contract observation over the client's active endpoint topology.</summary>
/// <remarks>This snapshot is diagnostic evidence, not a lease or a guarantee that the next call will be admitted.</remarks>
public sealed record ContractReadinessSnapshot
{
    /// <summary>Creates an immutable scalar observation of contract readiness.</summary>
    /// <param name="requirement">The requirement applied to every current session.</param>
    /// <param name="state">The aggregate observed state.</param>
    /// <param name="compatibleEndpoints">The number of distinct endpoint identities with compatible sessions.</param>
    /// <param name="endpoints">Per-session observations, including unavailable current endpoints.</param>
    public ContractReadinessSnapshot(
        ContractRequirement requirement,
        ContractObservationState state,
        int compatibleEndpoints,
        IReadOnlyList<ContractEndpointObservation> endpoints)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentOutOfRangeException.ThrowIfNegative(compatibleEndpoints);
        Requirement = requirement;
        State = state;
        CompatibleEndpoints = compatibleEndpoints;
        var copy = new ContractEndpointObservation[endpoints.Count];
        for (var index = 0; index < copy.Length; index++)
            copy[index] = endpoints[index];
        Endpoints = Array.AsReadOnly(copy);
    }

    /// <summary>Gets the scalar requirement applied to this observation.</summary>
    public ContractRequirement Requirement { get; }

    /// <summary>Gets the aggregate observed availability state.</summary>
    public ContractObservationState State { get; }

    /// <summary>Gets the number of distinct compatible endpoints, independent of connection-pool size.</summary>
    public int CompatibleEndpoints { get; }

    /// <summary>Gets immutable scalar observations from current topology endpoints and sessions.</summary>
    public IReadOnlyList<ContractEndpointObservation> Endpoints { get; }
}

/// <summary>Reports a contract admission failure together with its point-in-time readiness evidence.</summary>
public sealed class SharpLinkContractAdmissionException : SharpLinkException
{
    /// <summary>Creates a contract admission failure with scalar observation details.</summary>
    /// <param name="code">The stable SharpLink error code.</param>
    /// <param name="message">The diagnostic failure message.</param>
    /// <param name="snapshot">The readiness observation associated with the failure.</param>
    public SharpLinkContractAdmissionException(
        SharpLinkErrorCode code,
        string message,
        ContractReadinessSnapshot snapshot)
        : base(code, message)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
    }

    /// <summary>Gets the point-in-time scalar observation associated with the admission failure.</summary>
    public ContractReadinessSnapshot Snapshot { get; }
}
