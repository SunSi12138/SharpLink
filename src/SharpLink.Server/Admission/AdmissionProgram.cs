namespace SharpLink.Server;

/// <summary>
/// Immutable admission-policy publication for one runtime generation. Requests capture one
/// publication at the RequestLoop boundary and never re-read the server's current publication.
/// </summary>
internal sealed class AdmissionProgram
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        SharpLinkAdmissionController,
        AdmissionProgram> ProgramsByController = new();
    private static long s_nextGenerationId;

    private readonly SharpLinkAdmissionController? _controller;
    private int _activeUses;
    private int _duplicateReleaseAttempts;

    private AdmissionProgram(long sentinelGenerationId)
        => GenerationId = sentinelGenerationId;

    internal AdmissionProgram(SharpLinkAdmissionController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        GenerationId = Interlocked.Increment(ref s_nextGenerationId);
        ProgramsByController.Add(controller, this);
    }

    internal static AdmissionProgram Uninitialized { get; } = new(long.MinValue);

    internal static AdmissionProgram Disabled { get; } = new(0);

    internal long GenerationId { get; }

    internal bool IsEnabled => _controller is not null;

    internal SharpLinkAdmissionController Controller
        => _controller ?? throw new InvalidOperationException("Disabled admission has no controller.");

    internal bool QueueOneWayCalls => Controller.QueueOneWayCalls;

    internal int ActiveUses => Volatile.Read(ref _activeUses);

    internal int DuplicateReleaseAttempts => Volatile.Read(ref _duplicateReleaseAttempts);

    internal static AdmissionProgram FromController(SharpLinkAdmissionController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return ProgramsByController.TryGetValue(controller, out var program)
            ? program
            : throw new InvalidOperationException("Admission controller has no published program generation.");
    }

    internal void AcquireUse()
    {
        if (!IsEnabled)
            throw new InvalidOperationException("Disabled admission does not acquire generation uses.");
        Interlocked.Increment(ref _activeUses);
    }

    internal void ReleaseUse()
    {
        if (Interlocked.Decrement(ref _activeUses) >= 0)
            return;

        // Restore accounting before surfacing an ownership bug so diagnostics stay stable.
        Interlocked.Increment(ref _activeUses);
        Interlocked.Increment(ref _duplicateReleaseAttempts);
        throw new InvalidOperationException("Admission program use count underflowed.");
    }
}
