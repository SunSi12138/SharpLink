namespace SharpLink.Abstractions;

/// <summary>Controls how this endpoint takes part in version changes.</summary>
public enum SharpLinkVersionCoordinationMode
{
    /// <summary>Does not initiate or approve coordinated version changes.</summary>
    Disabled,
    /// <summary>Requires application approval for each proposed change.</summary>
    Manual,
    /// <summary>Approves an explicitly joined attempt when the required local assets are ready.</summary>
    Automatic
}

/// <summary>Identifies which endpoint proposes the coordinated change.</summary>
public enum SharpLinkVersionInitiator
{
    /// <summary>The client proposes the change for a candidate staged by the server.</summary>
    Client = 1,
    /// <summary>The server proposes its staged candidate.</summary>
    Server
}

/// <summary>Facts about a single proposal on a physical session. These values confer no admission rights.</summary>
public readonly record struct SharpLinkVersionChange(
    ulong TransitionId, SharpLinkVersionSession Session, SharpLinkVersionInitiator Initiator,
    SharpLinkVersionRegistration Current, SharpLinkVersionRegistration Candidate);

/// <summary>Why the endpoints did not agree to a change.</summary>
public enum SharpLinkVersionDeclineReason
{
    /// <summary>No refusal was reported. Inspect the operation result to determine what ran.</summary>
    None,
    /// <summary>A local policy did not approve this attempt.</summary>
    Policy,
    /// <summary>The required local registration was not available.</summary>
    MissingLocalRegistration,
    /// <summary>The local registration did not match the proposed contract hash.</summary>
    IncompatibleLocalRegistration,
    /// <summary>The original prepared route was no longer available to the attempt.</summary>
    SourceUnavailable,
    /// <summary>The publications or readiness needed for the proposal changed.</summary>
    CandidateChanged,
    /// <summary>The peer disabled version coordination.</summary>
    PeerDisabled,
    /// <summary>The peer requires an explicit manual decision and did not enroll.</summary>
    ApprovalRequired,
    /// <summary>The peer did not join the attempt.</summary>
    PeerNotEnrolled
}

/// <summary>Reports what the client achieved for the named proposal.</summary>
public readonly record struct SharpLinkClientVersionChangeResult(
    SharpLinkVersionChange Change, SharpLinkVersionDeclineReason DeclineReason, Guid? LocalRegistrationId)
{
    /// <summary>Gets whether the client published a prepared route to the exact new registration.</summary>
    public bool Resumed => LocalRegistrationId.HasValue;
}

/// <summary>Selects a physical peer and its proposer role for a coordinated replacement.</summary>
public readonly record struct SharpLinkVersionParticipant(SharpLinkVersionSession Session, SharpLinkVersionInitiator Initiator);

/// <summary>Reports what the server sent to one selected peer.</summary>
/// <param name="Session">The selected physical session.</param>
/// <param name="Change">The staged proposal, or null if staging did not reach this peer.</param>
/// <param name="DeclineReason">The observed refusal, if any.</param>
/// <param name="PreparedClientRegistrationId">The client registration verified by the server after Ready.</param>
/// <param name="ActivationSent">Whether the server sent Activate, not whether the client resumed.</param>
public readonly record struct SharpLinkVersionPeerResult(
    SharpLinkVersionSession Session, SharpLinkVersionChange? Change, SharpLinkVersionDeclineReason DeclineReason,
    Guid? PreparedClientRegistrationId, bool ActivationSent);

/// <summary>Reports server publication and drain independently of per-peer activation.</summary>
public sealed class SharpLinkServerVersionChangeResult
{
    internal SharpLinkServerVersionChangeResult(SharpLinkAssemblyReplacementResult replacement, SharpLinkVersionPeerResult[] peers)
        => (Replacement, Peers) = (replacement, Array.AsReadOnly(peers));

    /// <summary>Gets the local publication and old-registration drain result.</summary>
    public SharpLinkAssemblyReplacementResult Replacement { get; }

    /// <summary>Gets the results in participant order. Sending Activate does not confirm remote resume.</summary>
    public IReadOnlyList<SharpLinkVersionPeerResult> Peers { get; }
}

/// <summary>Reports a local observation about a proposed or completed change.</summary>
public enum SharpLinkVersionChangeStage
{
    /// <summary>The endpoint observed a proposed change.</summary>
    Proposed,
    /// <summary>The server published the new registration.</summary>
    Published,
    /// <summary>The client prepared its new route, or the server sent activation and settled its local drain wait.</summary>
    Completed,
    /// <summary>The attempt ended with a refusal.</summary>
    Declined,
    /// <summary>The attempt did not complete. Inspect local inventory for the current publication.</summary>
    Failed
}

/// <summary>A best-effort notification. Query inventory to recover missing updates.</summary>
/// <param name="Stage">The observed stage; Completed is local to the observing endpoint.</param>
/// <param name="Change">The proposal this notification describes.</param>
/// <param name="LocalPublicationOccurred">Whether this endpoint published the replacement or prepared route.</param>
/// <param name="DeclineReason">The observed refusal, if any.</param>
public readonly record struct SharpLinkVersionChangeNotice(
    SharpLinkVersionChangeStage Stage, SharpLinkVersionChange Change, bool LocalPublicationOccurred,
    SharpLinkVersionDeclineReason DeclineReason);

/// <summary>The server's response to a client declaration of an already installed local target.</summary>
public enum SharpLinkVersionDeclarationStatus
{
    /// <summary>The server does not publish the requested contract.</summary>
    Missing,
    /// <summary>The published contract hash differs from the client's declaration.</summary>
    Incompatible,
    /// <summary>The server has disabled version coordination.</summary>
    Disabled,
    /// <summary>Server policy requires a separate coordinated attempt with manual approval.</summary>
    ApprovalRequired,
    /// <summary>The session or matching registration is not ready.</summary>
    NotReady,
    /// <summary>The server observed a compatible publication without changing it.</summary>
    Observed
}

/// <summary>A declaration records observations only. It does not publish, approve, or bind a registration.</summary>
public readonly record struct SharpLinkVersionDeclarationResult(
    SharpLinkVersionSession Session, SharpLinkVersionRegistration LocalRegistration,
    SharpLinkVersionDeclarationStatus Status, SharpLinkVersionRegistration? ObservedServerRegistration);
