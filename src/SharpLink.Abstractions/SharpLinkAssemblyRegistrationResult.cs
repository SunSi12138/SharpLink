namespace SharpLink.Abstractions;

/// <summary>Identifies why a runtime assembly registration was rejected.</summary>
public enum SharpLinkAssemblyRegistrationErrorCode
{
    /// <summary>The supplied argument is invalid.</summary>
    InvalidArgument,
    /// <summary>The client or server cannot accept registry changes in its current state.</summary>
    InvalidObjectState,
    /// <summary>Runtime assembly registration is unavailable on this platform.</summary>
    PlatformNotSupported,
    /// <summary>The assembly does not contain a generated manifest locator.</summary>
    MissingManifest,
    /// <summary>The manifest is malformed or cannot be activated.</summary>
    InvalidManifest,
    /// <summary>The manifest API, protocol, or generator version is incompatible.</summary>
    IncompatibleManifest,
    /// <summary>The same Assembly object is already registered.</summary>
    DuplicateAssembly,
    /// <summary>A declared generated dependency is not registered.</summary>
    MissingDependency,
    /// <summary>A contract descriptor conflicts with an existing owner.</summary>
    ContractConflict,
    /// <summary>A method descriptor conflicts with an existing route.</summary>
    MethodConflict,
    /// <summary>A generated Codec conflicts with an existing schema owner.</summary>
    CodecConflict,
    /// <summary>An RPC service conflicts with an existing service owner.</summary>
    ServiceConflict,
    /// <summary>An internal registry safety limit was reached.</summary>
    CapacityExceeded
}

/// <summary>Contains a stable error code and reference-free diagnostic details.</summary>
/// <param name="Code">The machine-readable failure category.</param>
/// <param name="Message">The complete human-readable diagnostic.</param>
/// <param name="IncomingAssembly">The incoming assembly identity, when known.</param>
/// <param name="ExistingAssembly">The existing owner assembly identity, when known.</param>
/// <param name="IncomingLoadContext">The incoming AssemblyLoadContext identity, when known.</param>
/// <param name="ExistingLoadContext">The existing AssemblyLoadContext identity, when known.</param>
/// <param name="Artifact">The conflicting artifact kind, when known.</param>
/// <param name="ContractName">The contract name, when known.</param>
/// <param name="ContractId">The contract ID, when known.</param>
/// <param name="MethodName">The method name, when known.</param>
/// <param name="MethodId">The method ID, when known.</param>
/// <param name="ExistingFingerprint">The existing fingerprint, when known.</param>
/// <param name="IncomingFingerprint">The incoming fingerprint, when known.</param>
public sealed record SharpLinkAssemblyRegistrationError(
    SharpLinkAssemblyRegistrationErrorCode Code,
    string Message,
    string? IncomingAssembly = null,
    string? ExistingAssembly = null,
    string? IncomingLoadContext = null,
    string? ExistingLoadContext = null,
    string? Artifact = null,
    string? ContractName = null,
    long? ContractId = null,
    string? MethodName = null,
    long? MethodId = null,
    string? ExistingFingerprint = null,
    string? IncomingFingerprint = null);

/// <summary>Reports whether a runtime assembly was atomically registered.</summary>
public readonly record struct SharpLinkAssemblyRegistrationResult
{
    internal SharpLinkAssemblyRegistrationResult(
        bool succeeded,
        SharpLinkAssemblyRegistrationError? error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    /// <summary>Gets whether the complete manifest was published.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the rejection reason, or <see langword="null"/> after success.</summary>
    public SharpLinkAssemblyRegistrationError? Error { get; }

    internal static SharpLinkAssemblyRegistrationResult Success() => new(true, null);

    internal static SharpLinkAssemblyRegistrationResult Failure(SharpLinkAssemblyRegistrationError error)
        => new(false, error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>Reports the state reached by an assembly unregister operation.</summary>
public readonly record struct SharpLinkAssemblyUnregisterResult
{
    /// <summary>Gets whether SharpLink released all framework-owned references.</summary>
    public bool ReferencesReleased { get; init; }

    /// <summary>Gets the number of calls still using the module.</summary>
    public int RemainingCalls { get; init; }

    /// <summary>Gets the number of streams still using the module.</summary>
    public int RemainingStreams { get; init; }
}

/// <summary>Reports whether a replacement was published and how far the old registration drained.</summary>
/// <remarks>
/// <para>A successful publication is never rolled back when the caller cancels its wait or the graceful timeout expires.</para>
/// <para>When <see cref="ReferencesReleased"/> is <see langword="false"/>, SharpLink completes cleanup after the last old call or stream exits.</para>
/// </remarks>
public readonly record struct SharpLinkAssemblyReplacementResult
{
    /// <summary>Gets whether the new registration was atomically published.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets the preparation or publication rejection, or <see langword="null"/> after publication.</summary>
    public SharpLinkAssemblyRegistrationError? Error { get; init; }

    /// <summary>Gets whether SharpLink released all framework-owned references to the old registration before returning.</summary>
    public bool ReferencesReleased { get; init; }

    /// <summary>Gets the number of old calls still running when the bounded drain returned.</summary>
    public int RemainingCalls { get; init; }

    /// <summary>Gets the number of old streams still running when the bounded drain returned.</summary>
    public int RemainingStreams { get; init; }

    internal static SharpLinkAssemblyReplacementResult Failure(SharpLinkAssemblyRegistrationError error)
        => new() { Error = error ?? throw new ArgumentNullException(nameof(error)) };

    internal static SharpLinkAssemblyReplacementResult Published(SharpLinkAssemblyUnregisterResult drain)
        => new()
        {
            Succeeded = true,
            ReferencesReleased = drain.ReferencesReleased,
            RemainingCalls = drain.RemainingCalls,
            RemainingStreams = drain.RemainingStreams
        };
}
