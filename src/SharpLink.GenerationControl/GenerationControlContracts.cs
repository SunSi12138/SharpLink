using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.GenerationControl;

/// <summary>Describes how the current endpoint can materialize a desired generation.</summary>
public enum SharpLinkGenerationMaterializationMode
{
    /// <summary>The endpoint has not declared a materialization capability.</summary>
    Unknown = 0,
    /// <summary>The endpoint can stage and activate a generation without replacing the process.</summary>
    HotReplaceSupported = 1,
    /// <summary>The endpoint requires process or binary replacement to activate a different generation.</summary>
    ProcessReplacementRequired = 2,
    /// <summary>The endpoint cannot materialize the requested generation.</summary>
    Unsupported = 3
}

/// <summary>Describes the lifecycle state of one materialized generation.</summary>
public enum SharpLinkGenerationState
{
    /// <summary>The generation is not materialized on this endpoint.</summary>
    Absent = 0,
    /// <summary>The generation artifact has been staged but not fully validated.</summary>
    Staged = 1,
    /// <summary>The staged generation passed application/provider validation.</summary>
    Validated = 2,
    /// <summary>The generation is prepared and can be activated.</summary>
    Ready = 3,
    /// <summary>The generation is the endpoint's active generation for its capability.</summary>
    Active = 4,
    /// <summary>The generation is retired from new ownership while existing work drains.</summary>
    Draining = 5,
    /// <summary>The generation was removed from endpoint ownership.</summary>
    Removed = 6,
    /// <summary>The generation could not be prepared or activated.</summary>
    Failed = 7
}

/// <summary>Describes the structured outcome of a generation-control operation.</summary>
public enum SharpLinkGenerationOperationStatus
{
    /// <summary>The operation committed the requested state change.</summary>
    Succeeded = 0,
    /// <summary>The endpoint already satisfied the requested state.</summary>
    AlreadySatisfied = 1,
    /// <summary>The desired generation requires process or binary replacement.</summary>
    ProcessReplacementRequired = 2,
    /// <summary>The endpoint rejected the requested transition as an expected control-plane outcome.</summary>
    Rejected = 3,
    /// <summary>The endpoint does not support the requested operation or generation.</summary>
    Unsupported = 4
}

/// <summary>Identifies one capability generation without imposing module- or assembly-specific semantics.</summary>
public sealed class SharpLinkGenerationIdentity
{
    /// <summary>Gets or initializes the stable application-defined capability identifier.</summary>
    public string CapabilityId { get; init; } = string.Empty;

    /// <summary>Gets or initializes the stable generation identifier within the capability.</summary>
    public string GenerationId { get; init; } = string.Empty;
}

/// <summary>Describes a desired generation and the identities needed to reconcile it safely.</summary>
public sealed class SharpLinkGenerationDescriptor
{
    /// <summary>Gets or initializes the capability and generation identity.</summary>
    public SharpLinkGenerationIdentity Identity { get; init; } = new();

    /// <summary>Gets or initializes the application-defined wire or contract identity.</summary>
    public string WireIdentity { get; init; } = string.Empty;

    /// <summary>Gets or initializes the generated ABI identity expected by the generation.</summary>
    public string GeneratedAbiIdentity { get; init; } = string.Empty;

    /// <summary>Gets or initializes the content hash or immutable artifact identity.</summary>
    public string ArtifactHash { get; init; } = string.Empty;

    /// <summary>Gets or initializes an application-defined artifact reference such as a URI, object key, or deployment ID.</summary>
    public string ArtifactReference { get; init; } = string.Empty;

    /// <summary>Gets or initializes opaque application compatibility metadata used during provider validation.</summary>
    public string CompatibilityMetadata { get; init; } = string.Empty;
}

/// <summary>Captures the actual lifecycle state of one generation on an endpoint.</summary>
public sealed class SharpLinkGenerationSnapshot
{
    /// <summary>Gets or initializes the generation descriptor.</summary>
    public SharpLinkGenerationDescriptor Descriptor { get; init; } = new();

    /// <summary>Gets or initializes the current generation lifecycle state.</summary>
    public SharpLinkGenerationState State { get; init; }

    /// <summary>Gets or initializes an optional stable application/provider diagnostic code.</summary>
    public string DiagnosticCode { get; init; } = string.Empty;
}

/// <summary>Provides the queryable desired/actual source of truth for generation reconciliation.</summary>
public sealed class SharpLinkGenerationInventory
{
    /// <summary>Gets or initializes the monotonic endpoint-local revision for this inventory snapshot.</summary>
    public long Revision { get; init; }

    /// <summary>Gets or initializes the endpoint materialization capability.</summary>
    public SharpLinkGenerationMaterializationMode MaterializationMode { get; init; }

    /// <summary>Gets or initializes the desired generation set.</summary>
    public SharpLinkGenerationDescriptor[] Desired { get; init; } = [];

    /// <summary>Gets or initializes the actual generation set.</summary>
    public SharpLinkGenerationSnapshot[] Actual { get; init; } = [];
}

/// <summary>Requests activation of a previously staged generation.</summary>
public sealed class SharpLinkGenerationActivationRequest
{
    /// <summary>Gets or initializes the generation to activate.</summary>
    public SharpLinkGenerationIdentity Identity { get; init; } = new();
}

/// <summary>Reports a structured stage or activation outcome.</summary>
public sealed class SharpLinkGenerationOperationResult
{
    /// <summary>Gets or initializes the operation status.</summary>
    public SharpLinkGenerationOperationStatus Status { get; init; }

    /// <summary>Gets or initializes the inventory revision after the operation outcome was observed.</summary>
    public long Revision { get; init; }

    /// <summary>Gets or initializes whether <see cref="Snapshot"/> contains an operation-specific generation snapshot.</summary>
    public bool HasSnapshot { get; init; }

    /// <summary>Gets or initializes the operation-specific generation snapshot.</summary>
    public SharpLinkGenerationSnapshot Snapshot { get; init; } = new();

    /// <summary>Gets or initializes a human-readable diagnostic that callers must not parse for branching.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>Notifies a watcher that the queryable generation inventory changed.</summary>
public sealed class SharpLinkGenerationChange
{
    /// <summary>Gets or initializes the new endpoint-local inventory revision.</summary>
    public long Revision { get; init; }

    /// <summary>Gets or initializes the affected capability, or an empty value when multiple capabilities changed.</summary>
    public string CapabilityId { get; init; } = string.Empty;
}

/// <summary>Stable optional RPC control contract for cross-endpoint generation reconciliation.</summary>
/// <remarks>
/// Inventory queries are the source of truth. <see cref="WatchAsync"/> is only an invalidation fast path:
/// consumers must re-query after reconnects, missed notifications, or revision gaps.
/// </remarks>
[RpcContract]
public interface ISharpLinkGenerationControl : IService
{
    /// <summary>Returns the endpoint's current desired/actual generation inventory.</summary>
    ValueTask<SharpLinkGenerationInventory> GetInventoryAsync(CancellationToken cancellationToken);

    /// <summary>Stages and validates a desired generation without making activation a single-step update.</summary>
    ValueTask<SharpLinkGenerationOperationResult> StageAsync(
        SharpLinkGenerationDescriptor descriptor,
        CancellationToken cancellationToken);

    /// <summary>Activates a generation that was prepared according to the endpoint provider's policy.</summary>
    ValueTask<SharpLinkGenerationOperationResult> ActivateAsync(
        SharpLinkGenerationActivationRequest request,
        CancellationToken cancellationToken);

    /// <summary>Watches endpoint-local inventory revisions after <paramref name="afterRevision"/>.</summary>
    IAsyncEnumerable<SharpLinkGenerationChange> WatchAsync(
        long afterRevision,
        CancellationToken cancellationToken);
}

/// <summary>Separates generation reconciliation from application-specific artifact materialization.</summary>
public interface ISharpLinkGenerationProvider
{
    /// <summary>Gets the endpoint's materialization capability.</summary>
    SharpLinkGenerationMaterializationMode MaterializationMode { get; }

    /// <summary>Stages and validates an application-defined artifact for the desired generation.</summary>
    ValueTask<SharpLinkGenerationProviderResult> StageAsync(
        SharpLinkGenerationDescriptor descriptor,
        CancellationToken cancellationToken);

    /// <summary>Activates a previously staged generation using the provider's runtime-specific mechanism.</summary>
    ValueTask<SharpLinkGenerationProviderResult> ActivateAsync(
        SharpLinkGenerationActivationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Reports the provider-level outcome before the control service publishes endpoint inventory.</summary>
public sealed class SharpLinkGenerationProviderResult
{
    /// <summary>Gets or initializes the structured provider outcome.</summary>
    public SharpLinkGenerationOperationStatus Status { get; init; }

    /// <summary>Gets or initializes the resulting generation state when the provider can report one.</summary>
    public SharpLinkGenerationState State { get; init; }

    /// <summary>Gets or initializes an optional stable application/provider diagnostic code.</summary>
    public string DiagnosticCode { get; init; } = string.Empty;

    /// <summary>Gets or initializes a human-readable diagnostic that callers must not parse for branching.</summary>
    public string Message { get; init; } = string.Empty;
}
