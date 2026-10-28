using System.Reflection;

namespace SharpLink.Abstractions;

/// <summary>Identifies one authorized client runtime in a fixed maintenance cohort.</summary>
/// <param name="ParticipantId">Stable identity assigned by the host.</param>
/// <param name="RuntimeIncarnation">Exact identity of this running client instance, not a credential.</param>
/// <param name="AuthenticatedSubject">Subject that existing server authentication must establish.</param>
public sealed record SharpLinkMaintenanceParticipant(
    Guid ParticipantId,
    Guid RuntimeIncarnation,
    string AuthenticatedSubject);

/// <summary>Configures a fixed-endpoint, TLS-authenticated maintenance participant before connecting.</summary>
/// <param name="DomainId">Explicit identity of the trusted server maintenance domain.</param>
/// <param name="ParticipantId">Identity listed by that server host.</param>
/// <param name="RuntimeIncarnation">Fresh identity for this particular client runtime.</param>
public sealed record SharpLinkClientMaintenanceOptions(
    Guid DomainId,
    Guid ParticipantId,
    Guid RuntimeIncarnation);

/// <summary>Describes one compatible implementation-only replacement; client contracts stay immutable.</summary>
/// <param name="Source">Exact currently registered implementation assembly.</param>
/// <param name="Target">Locally loaded compatible replacement assembly.</param>
public sealed record SharpLinkImplementationReplacement(Assembly Source, Assembly Target);

/// <summary>Observable stages of a cooperative, stop-the-world implementation update.</summary>
public enum SharpLinkMaintenanceStage
{
    /// <summary>The host is validating the complete domain and replacement set.</summary>
    Validating,
    /// <summary>Client publication is frozen and ingress fences are being collected.</summary>
    Freezing,
    /// <summary>Old physical execution, disposal and send ownership is being joined.</summary>
    Draining,
    /// <summary>Both directional fences and the final ordinary acknowledgements were observed.</summary>
    Quiescent,
    /// <summary>Old implementations are being irreversibly retired and released.</summary>
    Retiring,
    /// <summary>The target is committed, but no participant has permission to resume.</summary>
    CommittedPaused,
    /// <summary>All participants committed and the host is restoring availability.</summary>
    Resuming,
    /// <summary>The same complete physical-session cohort is available on the target implementation.</summary>
    Succeeded,
    /// <summary>Validation rejected the operation before the domain was frozen.</summary>
    RejectedBeforeSeal,
    /// <summary>The domain stays unavailable; automatic rollback is prohibited.</summary>
    FailedClosed
}

/// <summary>The terminal, local-host receipt for an implementation maintenance operation.</summary>
/// <param name="OperationId">Exact operation identity.</param>
/// <param name="PlanDigest">SHA-256 correlation identity of the bound plan, not artifact integrity attestation.</param>
/// <param name="Stage">Terminal stage.</param>
/// <param name="TargetGeneration">Expected committed server catalog generation.</param>
/// <param name="Failure">Failure detail, or null for success.</param>
public sealed record SharpLinkMaintenanceResult(
    Guid OperationId,
    string PlanDigest,
    SharpLinkMaintenanceStage Stage,
    long TargetGeneration,
    Exception? Failure);

/// <summary>Observes a host-owned operation. Cancelling a wait never cancels or resumes the operation.</summary>
public interface ISharpLinkMaintenanceOperation
{
    /// <summary>Gets the immutable operation identity.</summary>
    Guid OperationId { get; }
    /// <summary>Gets the immutable SHA-256 plan correlation identity, not an artifact integrity hash.</summary>
    string PlanDigest { get; }
    /// <summary>Gets the current stage.</summary>
    SharpLinkMaintenanceStage Stage { get; }
    /// <summary>Waits for a terminal result without transferring cancellation to the operation.</summary>
    Task<SharpLinkMaintenanceResult> WaitAsync(CancellationToken cancellationToken = default);
}

/// <summary>Host-only authority for a complete server domain. Never register this as an RPC service.</summary>
public interface ISharpLinkMaintenanceController
{
    /// <summary>Gets the currently published server registry generation.</summary>
    long CatalogGeneration { get; }
    /// <summary>Begins a compatible implementation update while preserving every enrolled physical session.</summary>
    /// <param name="expectedGeneration">Exact catalog generation the caller intends to replace.</param>
    /// <param name="replacements">Complete, locally loaded implementation replacement set.</param>
    ISharpLinkMaintenanceOperation BeginMaintenance(
        long expectedGeneration,
        IReadOnlyList<SharpLinkImplementationReplacement> replacements);
}
