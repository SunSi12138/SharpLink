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

    internal AdmissionProgramUse AcquireUse()
    {
        if (!IsEnabled)
            throw new InvalidOperationException("Disabled admission does not acquire generation uses.");
        Interlocked.Increment(ref _activeUses);
        return new AdmissionProgramUse(this);
    }

    internal void ReleaseUse()
    {
        if (Interlocked.Decrement(ref _activeUses) < 0)
            throw new InvalidOperationException("Admission program use count underflowed.");
    }

    internal void RecordDuplicateReleaseAttempt()
        => Interlocked.Increment(ref _duplicateReleaseAttempts);
}

/// <summary>
/// Exactly-once lifetime token for one captured admission generation. The token may be transferred
/// to the existing server-call lifetime owner without rebuilding policy or routing state.
/// </summary>
internal sealed class AdmissionProgramUse : IDisposable
{
    private readonly AdmissionProgram _program;
    private int _disposed;

    internal AdmissionProgramUse(AdmissionProgram program)
        => _program = program ?? throw new ArgumentNullException(nameof(program));

    internal AdmissionProgram Program => _program;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            _program.RecordDuplicateReleaseAttempt();
            return;
        }

        _program.ReleaseUse();
    }
}
