using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.GenerationControl;

/// <summary>Describes how the local endpoint can materialize a generation when its reconciler decides to follow a peer.</summary>
public enum SharpLinkGenerationMaterializationMode
{
    /// <summary>The local endpoint has not declared a materialization capability.</summary>
    Unknown = 0,
    /// <summary>The local endpoint can stage and activate a generation without replacing the process.</summary>
    HotReplaceSupported = 1,
    /// <summary>The local endpoint requires process or binary replacement to activate a different generation.</summary>
    ProcessReplacementRequired = 2,
    /// <summary>The local endpoint cannot materialize the requested generation.</summary>
    Unsupported = 3
}

/// <summary>Describes the endpoint-local lifecycle state of one generation.</summary>
public enum SharpLinkGenerationState
{
    /// <summary>The generation is not materialized on this endpoint.</summary>
    Absent = 0,
    /// <summary>The generation artifact has been staged but not fully validated.</summary>
    Staged = 1,
    /// <summary>The staged generation passed local provider validation.</summary>
    Validated = 2,
    /// <summary>The generation is prepared and can be activated.</summary>
    Ready = 3,
    /// <summary>The generation is active for its capability on this endpoint.</summary>
    Active = 4,
    /// <summary>The generation no longer accepts new ownership while existing work drains.</summary>
    Draining = 5,
    /// <summary>The generation was removed from endpoint ownership.</summary>
    Removed = 6,
    /// <summary>The local generation preparation or activation failed.</summary>
    Failed = 7
}

/// <summary>Describes a structured outcome from the local generation provider.</summary>
public enum SharpLinkGenerationOperationStatus
{
    /// <summary>The local provider committed the requested state change.</summary>
    Succeeded = 0,
    /// <summary>The local endpoint already satisfied the requested state.</summary>
    AlreadySatisfied = 1,
    /// <summary>The requested local generation requires process or binary replacement.</summary>
    ProcessReplacementRequired = 2,
    /// <summary>The local provider rejected the transition as an expected policy or compatibility outcome.</summary>
    Rejected = 3,
    /// <summary>The local provider does not support the requested operation or generation.</summary>
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

/// <summary>Describes one endpoint generation and the metadata a peer may use for local compatibility decisions.</summary>
public sealed class SharpLinkGenerationDescriptor
{
    /// <summary>Gets or initializes the capability and generation identity.</summary>
    public SharpLinkGenerationIdentity Identity { get; init; } = new();

    /// <summary>Gets or initializes the application-defined wire or contract identity.</summary>
    public string WireIdentity { get; init; } = string.Empty;

    /// <summary>Gets or initializes the generated ABI identity expected by the generation.</summary>
    public string GeneratedAbiIdentity { get; init; } = string.Empty;

    /// <summary>Gets or initializes the immutable artifact content hash or identity.</summary>
    public string ArtifactHash { get; init; } = string.Empty;

    /// <summary>Gets or initializes an application-defined artifact reference hint.</summary>
    public string ArtifactReference { get; init; } = string.Empty;

    /// <summary>Gets or initializes opaque compatibility metadata interpreted only by application policy.</summary>
    public string CompatibilityMetadata { get; init; } = string.Empty;
}

/// <summary>Declares one generation currently known by the endpoint.</summary>
public sealed class SharpLinkGenerationSnapshot
{
    /// <summary>Gets or initializes the generation descriptor.</summary>
    public SharpLinkGenerationDescriptor Descriptor { get; init; } = new();

    /// <summary>Gets or initializes the endpoint-local lifecycle state.</summary>
    public SharpLinkGenerationState State { get; init; }

    /// <summary>Gets or initializes an optional stable local diagnostic code.</summary>
    public string DiagnosticCode { get; init; } = string.Empty;
}

/// <summary>Provides the queryable source of truth for an endpoint's declared generation state.</summary>
public sealed class SharpLinkGenerationInventory
{
    /// <summary>Gets or initializes the monotonic endpoint-local revision for this declaration.</summary>
    public long Revision { get; init; }

    /// <summary>Gets or initializes the generations currently declared by this endpoint.</summary>
    public SharpLinkGenerationSnapshot[] Generations { get; init; } = [];
}

/// <summary>Stable optional peer contract for generation declaration and synchronization.</summary>
/// <remarks>
/// SharpLink RPC requests are Client-initiated. <see cref="SynchronizeAsync"/> maps logical peer
/// synchronization onto a duplex stream: the Client sends its inventories and the Server returns its inventories.
/// Neither direction can remotely mutate the peer.
/// </remarks>
[RpcContract]
public interface ISharpLinkGenerationControl : IService
{
    /// <summary>Returns the Server endpoint's current declared generation inventory.</summary>
    ValueTask<SharpLinkGenerationInventory> GetInventoryAsync(CancellationToken cancellationToken);

    /// <summary>Exchanges complete endpoint inventories over one Client-initiated duplex stream.</summary>
    IAsyncEnumerable<SharpLinkGenerationInventory> SynchronizeAsync(
        IAsyncEnumerable<SharpLinkGenerationInventory> clientInventories,
        CancellationToken cancellationToken);
}

/// <summary>Local materialization SPI used only after local reconciliation policy decides to change this endpoint.</summary>
public interface ISharpLinkGenerationProvider
{
    /// <summary>Gets the local endpoint's materialization capability.</summary>
    SharpLinkGenerationMaterializationMode MaterializationMode { get; }

    /// <summary>Stages an application-owned artifact locally without publishing it as active.</summary>
    ValueTask<SharpLinkGenerationProviderResult> StageAsync(
        SharpLinkGenerationDescriptor descriptor,
        CancellationToken cancellationToken);

    /// <summary>Validates a previously staged local generation.</summary>
    ValueTask<SharpLinkGenerationProviderResult> ValidateAsync(
        SharpLinkGenerationIdentity identity,
        CancellationToken cancellationToken);

    /// <summary>Activates a locally prepared generation.</summary>
    ValueTask<SharpLinkGenerationProviderResult> ActivateAsync(
        SharpLinkGenerationIdentity identity,
        CancellationToken cancellationToken);

    /// <summary>Starts draining a local generation from new ownership.</summary>
    ValueTask<SharpLinkGenerationProviderResult> DrainAsync(
        SharpLinkGenerationIdentity identity,
        CancellationToken cancellationToken);
}

/// <summary>Reports a local provider outcome before the application publishes a new endpoint inventory revision.</summary>
public sealed class SharpLinkGenerationProviderResult
{
    /// <summary>Gets or initializes the structured local provider outcome.</summary>
    public SharpLinkGenerationOperationStatus Status { get; init; }

    /// <summary>Gets or initializes the resulting endpoint-local generation state when available.</summary>
    public SharpLinkGenerationState State { get; init; }

    /// <summary>Gets or initializes an optional stable local provider diagnostic code.</summary>
    public string DiagnosticCode { get; init; } = string.Empty;

    /// <summary>Gets or initializes a human-readable diagnostic that callers must not parse for branching.</summary>
    public string Message { get; init; } = string.Empty;
}
