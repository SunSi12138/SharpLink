namespace SharpLink;

/// <summary>Identifies an expected rejection from a runtime configuration publication.</summary>
public enum SharpLinkRuntimeConfigurationUpdateFailureCode : byte
{
    /// <summary>No expected rejection occurred.</summary>
    None = 0,

    /// <summary>The runtime lifecycle no longer accepts configuration publication.</summary>
    LifecycleClosed = 1,

    /// <summary>The requested update conflicts with the currently active runtime mode.</summary>
    ModeConflict = 2,

    /// <summary>Another runtime publication changed the source state before this candidate could commit.</summary>
    PublicationConflict = 3,

    /// <summary>The active implementation does not expose this runtime configuration surface.</summary>
    UnsupportedByImplementation = 4,

    /// <summary>The candidate was valid but cannot be published by the current runtime configuration.</summary>
    CandidateRejected = 5
}

/// <summary>Reports whether a runtime configuration candidate was accepted and atomically published.</summary>
/// <remarks>
/// Argument validation, cancellation, internal invariant failures, and fatal runtime failures remain exceptions.
/// Callers should branch on <see cref="FailureCode"/> for expected control-plane rejection instead of parsing
/// <see cref="Message"/>.
/// </remarks>
public readonly record struct SharpLinkRuntimeConfigurationUpdateResult
{
    private SharpLinkRuntimeConfigurationUpdateResult(
        bool succeeded,
        SharpLinkRuntimeConfigurationUpdateFailureCode failureCode,
        string? message)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Message = message;
    }

    /// <summary>Gets whether the requested runtime configuration operation completed without an expected rejection.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets the machine-readable expected rejection, or <see cref="SharpLinkRuntimeConfigurationUpdateFailureCode.None"/> on success.</summary>
    public SharpLinkRuntimeConfigurationUpdateFailureCode FailureCode { get; init; }

    /// <summary>Gets an optional diagnostic message. Callers must not branch on this text.</summary>
    public string? Message { get; init; }

    /// <summary>Creates a successful runtime configuration result.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult Success() =>
        new(true, SharpLinkRuntimeConfigurationUpdateFailureCode.None, null);

    /// <summary>Creates an expected runtime configuration rejection.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult Failure(
        SharpLinkRuntimeConfigurationUpdateFailureCode failureCode,
        string? message = null)
    {
        if (failureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.None)
            throw new ArgumentOutOfRangeException(nameof(failureCode));
        return new SharpLinkRuntimeConfigurationUpdateResult(false, failureCode, message);
    }
}
