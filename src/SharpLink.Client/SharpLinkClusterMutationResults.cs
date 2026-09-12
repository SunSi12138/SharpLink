namespace SharpLink.Client;

/// <summary>Identifies an expected runtime multi-cluster mutation rejection.</summary>
public enum SharpLinkClusterMutationFailureCode
{
    /// <summary>No expected rejection occurred.</summary>
    None = 0,
    /// <summary>The requested cluster key is already published.</summary>
    AlreadyExists = 1,
    /// <summary>The requested cluster key is not published.</summary>
    NotFound = 2,
    /// <summary>Another control-plane lifecycle operation currently owns the mutation boundary.</summary>
    Busy = 3,
    /// <summary>The coordinator lifecycle no longer accepts the requested mutation.</summary>
    LifecycleClosed = 4,
    /// <summary>The candidate would conflict with an already published contract route.</summary>
    RouteConflict = 5,
    /// <summary>The mutation would exceed a configured cluster or connection-budget limit.</summary>
    CapacityExceeded = 6,
    /// <summary>A replacement candidate could not become remotely available before publication.</summary>
    CandidateUnavailable = 7
}

/// <summary>Reports whether a runtime cluster add was atomically published.</summary>
public readonly record struct SharpLinkClusterAddResult
{
    internal SharpLinkClusterAddResult(
        bool succeeded,
        SharpLinkClusterMutationFailureCode failureCode,
        string? message)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Message = message;
    }

    /// <summary>Gets whether the new cluster and its routes were atomically published.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets the machine-readable expected rejection, or <see cref="SharpLinkClusterMutationFailureCode.None"/> after success.</summary>
    public SharpLinkClusterMutationFailureCode FailureCode { get; init; }

    /// <summary>Gets an optional human-readable diagnostic. Callers must branch on <see cref="FailureCode"/> instead of this text.</summary>
    public string? Message { get; init; }

    internal static SharpLinkClusterAddResult Success() =>
        new(true, SharpLinkClusterMutationFailureCode.None, null);

    internal static SharpLinkClusterAddResult Failure(
        SharpLinkClusterMutationFailureCode failureCode,
        string message)
        => new(false, failureCode, message);
}

/// <summary>Reports replacement publication separately from bounded retirement of the old cluster.</summary>
public readonly record struct SharpLinkClusterReplacementResult
{
    internal SharpLinkClusterReplacementResult(
        bool succeeded,
        bool published,
        SharpLinkClusterMutationFailureCode failureCode,
        string? message,
        bool referencesReleased,
        bool forcedStop)
    {
        Succeeded = succeeded;
        Published = published;
        FailureCode = failureCode;
        Message = message;
        ReferencesReleased = referencesReleased;
        ForcedStop = forcedStop;
    }

    /// <summary>Gets whether the replacement transaction committed.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets whether the replacement cluster was atomically published.</summary>
    public bool Published { get; init; }

    /// <summary>Gets the machine-readable pre-publication rejection, or <see cref="SharpLinkClusterMutationFailureCode.None"/> after publication.</summary>
    public SharpLinkClusterMutationFailureCode FailureCode { get; init; }

    /// <summary>Gets an optional human-readable diagnostic. Callers must branch on <see cref="FailureCode"/> instead of this text.</summary>
    public string? Message { get; init; }

    /// <summary>Gets whether the retired child released its owned resources before the bounded wait returned.</summary>
    public bool ReferencesReleased { get; init; }

    /// <summary>Gets whether coordinator-owned cleanup continues after the bounded retirement wait.</summary>
    public bool ForcedStop { get; init; }

    internal static SharpLinkClusterReplacementResult Failure(
        SharpLinkClusterMutationFailureCode failureCode,
        string message)
        => new(false, false, failureCode, message, false, false);

    internal static SharpLinkClusterReplacementResult Success(bool referencesReleased)
        => new(
            true,
            true,
            SharpLinkClusterMutationFailureCode.None,
            null,
            referencesReleased,
            forcedStop: !referencesReleased);
}

/// <summary>Reports removal publication separately from bounded cleanup of the retired cluster.</summary>
public readonly record struct SharpLinkClusterRemovalResult
{
    internal SharpLinkClusterRemovalResult(
        bool succeeded,
        SharpLinkClusterMutationFailureCode failureCode,
        string? message,
        bool referencesReleased,
        bool forcedStop)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Message = message;
        ReferencesReleased = referencesReleased;
        ForcedStop = forcedStop;
    }

    /// <summary>Gets whether the slot and its routes were removed from the public snapshot.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets the machine-readable pre-publication rejection, or <see cref="SharpLinkClusterMutationFailureCode.None"/> after removal.</summary>
    public SharpLinkClusterMutationFailureCode FailureCode { get; init; }

    /// <summary>Gets an optional human-readable diagnostic. Callers must branch on <see cref="FailureCode"/> instead of this text.</summary>
    public string? Message { get; init; }

    /// <summary>Gets whether the retired child released its owned resources before the bounded wait returned.</summary>
    public bool ReferencesReleased { get; init; }

    /// <summary>Gets whether coordinator-owned cleanup continues after the bounded retirement wait.</summary>
    public bool ForcedStop { get; init; }

    internal static SharpLinkClusterRemovalResult Failure(
        SharpLinkClusterMutationFailureCode failureCode,
        string message)
        => new(false, failureCode, message, false, false);

    internal static SharpLinkClusterRemovalResult Success(bool referencesReleased)
        => new(
            true,
            SharpLinkClusterMutationFailureCode.None,
            null,
            referencesReleased,
            forcedStop: !referencesReleased);
}
