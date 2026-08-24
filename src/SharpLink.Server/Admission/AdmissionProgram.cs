namespace SharpLink.Server;

/// <summary>
/// Immutable admission-policy publication for one runtime generation. Requests capture one
/// publication at the RequestLoop boundary and never re-read the server's current publication.
/// Mutable limiter/accounting state is owned by the server-scoped <see cref="AdmissionStateKernel"/>.
/// </summary>
internal sealed class AdmissionProgram
{
    private const int RetiredMask = int.MinValue;
    private const int UseCountMask = int.MaxValue;
    private static long s_nextGenerationId;

    private readonly SharpLinkAdmissionController? _controller;
    private readonly AdmissionStateKernel? _kernel;
    private int _useState;
    private int _duplicateReleaseAttempts;
    private int _reclaimed;
    private int _reclaimCount;

    private AdmissionProgram(long sentinelGenerationId)
        => GenerationId = sentinelGenerationId;

    internal AdmissionProgram(SharpLinkAdmissionController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        if (!controller.IsEnabled)
            throw new InvalidOperationException("Disabled admission does not create a program generation.");
        _kernel = controller.Kernel;
        GenerationId = Interlocked.Increment(ref s_nextGenerationId);
        controller.AttachProgram(this);
        _kernel.RegisterProgram(this);
    }

    internal static AdmissionProgram Uninitialized { get; } = new(long.MinValue);

    internal static AdmissionProgram Disabled { get; } = new(0);

    internal long GenerationId { get; }

    internal bool IsEnabled => _controller is not null;

    internal SharpLinkAdmissionController Controller
        => _controller ?? throw new InvalidOperationException("Disabled admission has no controller.");

    internal AdmissionStateKernel Kernel
        => _kernel ?? throw new InvalidOperationException("Disabled admission has no state kernel.");

    internal bool QueueOneWayCalls => Controller.QueueOneWayCalls;

    internal int ActiveUses => Volatile.Read(ref _useState) & UseCountMask;

    internal bool IsRetired => (Volatile.Read(ref _useState) & RetiredMask) != 0;

    internal bool IsReclaimed => Volatile.Read(ref _reclaimed) != 0;

    internal int ReclaimCount => Volatile.Read(ref _reclaimCount);

    internal int DuplicateReleaseAttempts => Volatile.Read(ref _duplicateReleaseAttempts);

    /// <summary>
    /// Acquires one generation use only while this program is current. The retired bit and use
    /// count share one CAS word so retirement cannot become visible between the lifecycle check
    /// and the increment.
    /// </summary>
    internal bool TryAcquireUse()
    {
        if (!IsEnabled)
            return false;

        while (true)
        {
            var state = Volatile.Read(ref _useState);
            if ((state & RetiredMask) != 0)
                return false;
            if ((state & UseCountMask) == UseCountMask)
                throw new InvalidOperationException("Admission program use count overflowed.");
            if (Interlocked.CompareExchange(ref _useState, state + 1, state) == state)
                return true;
        }
    }

    internal void AcquireUse()
    {
        if (!TryAcquireUse())
            throw new InvalidOperationException("Retired admission program cannot acquire new generation uses.");
    }

    /// <summary>Transitions this publication to retired exactly once without cancelling existing users.</summary>
    internal bool Retire()
    {
        if (!IsEnabled)
            return false;

        while (true)
        {
            var state = Volatile.Read(ref _useState);
            if ((state & RetiredMask) != 0)
                return false;
            var retired = state | RetiredMask;
            if (Interlocked.CompareExchange(ref _useState, retired, state) != state)
                continue;

            Kernel.OnProgramRetired(this);
            if ((state & UseCountMask) == 0)
                Kernel.TryReclaimProgram(this);
            return true;
        }
    }

    internal void ReleaseUse()
    {
        while (true)
        {
            var state = Volatile.Read(ref _useState);
            var activeUses = state & UseCountMask;
            if (activeUses == 0)
            {
                Interlocked.Increment(ref _duplicateReleaseAttempts);
                throw new InvalidOperationException("Admission program use count underflowed.");
            }

            var next = (state & RetiredMask) | (activeUses - 1);
            if (Interlocked.CompareExchange(ref _useState, next, state) != state)
                continue;
            if (next == RetiredMask)
                Kernel.TryReclaimProgram(this);
            return;
        }
    }

    internal bool TryMarkReclaimed()
    {
        if (!IsRetired || ActiveUses != 0)
            return false;
        if (Interlocked.CompareExchange(ref _reclaimed, 1, 0) != 0)
            return false;
        Interlocked.Increment(ref _reclaimCount);
        return true;
    }
}
