using SharpLink.Abstractions;

namespace SharpLink;

/// <summary>Reports desired-session publication success or an expected control-plane rejection.</summary>
/// <remarks>
/// Argument validation, caller cancellation, generation exhaustion, internal invariant failures, and fatal runtime
/// failures remain exceptions. Expected lifecycle/implementation rejection is represented by <see cref="FailureCode"/>.
/// </remarks>
public readonly record struct SharpLinkServerDesiredSessionPublicationResult
{
    private SharpLinkServerDesiredSessionPublicationResult(
        bool succeeded,
        SharpLinkRuntimeConfigurationUpdateFailureCode failureCode,
        SharpLinkServerDesiredSessionSnapshot? snapshot,
        string? message)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Snapshot = snapshot;
        Message = message;
    }

    /// <summary>Gets whether the desired configuration was accepted.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the stable expected-rejection code, or <see cref="SharpLinkRuntimeConfigurationUpdateFailureCode.None"/> on success.</summary>
    public SharpLinkRuntimeConfigurationUpdateFailureCode FailureCode { get; }

    /// <summary>Gets the immutable desired-session snapshot when publication succeeds.</summary>
    public SharpLinkServerDesiredSessionSnapshot? Snapshot { get; }

    /// <summary>Gets an optional diagnostic message. Callers must not branch on this text.</summary>
    public string? Message { get; }

    /// <summary>Creates a successful result.</summary>
    public static SharpLinkServerDesiredSessionPublicationResult Success(
        SharpLinkServerDesiredSessionSnapshot snapshot)
        => new(true, SharpLinkRuntimeConfigurationUpdateFailureCode.None, snapshot, null);

    /// <summary>Creates an expected-rejection result.</summary>
    public static SharpLinkServerDesiredSessionPublicationResult Failure(
        SharpLinkRuntimeConfigurationUpdateFailureCode failureCode,
        string? message = null)
    {
        if (failureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.None)
            throw new ArgumentOutOfRangeException(nameof(failureCode));
        return new(false, failureCode, null, message);
    }
}
