namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private static Action<SharpLinkServer, long, AdmissionProgram>? s_afterAdmissionPublicationReadForTests;
    private static Action<SharpLinkServer, long, AdmissionProgram?>? s_afterAdmissionCaptureForTests;

    private AdmissionProgram _admissionProgram = AdmissionProgram.Uninitialized;

    /// <summary>
    /// Deterministic stale-read probe. It runs after the current publication pointer is read and
    /// before TryAcquireUse performs the retired-bit/use-count CAS.
    /// </summary>
    internal static Action<SharpLinkServer, long, AdmissionProgram>? AfterAdmissionPublicationReadForTests
    {
        get => Volatile.Read(ref s_afterAdmissionPublicationReadForTests);
        set => Volatile.Write(ref s_afterAdmissionPublicationReadForTests, value);
    }

    internal static Action<SharpLinkServer, long, AdmissionProgram?>? AfterAdmissionCaptureForTests
    {
        get => Volatile.Read(ref s_afterAdmissionCaptureForTests);
        set => Volatile.Write(ref s_afterAdmissionCaptureForTests, value);
    }

    internal AdmissionProgram? CurrentAdmissionProgramForTests
    {
        get
        {
            var publication = ReadAdmissionPublication();
            return publication.IsEnabled ? publication : null;
        }
    }

    internal AdmissionProgram? OwnedAdmissionProgramForTests => _admissionController?.Program;

    internal AdmissionStateKernel? AdmissionStateKernelForTests => _admissionController?.Kernel;

    internal AdmissionProgram? CaptureAdmissionProgramForTests(long requestId = 0)
        => CaptureAdmissionProgram(requestId);

    internal AdmissionProgram CreateAdmissionProgramForTests(
        Action<SharpLinkAdmissionControlOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var controller = _admissionController ??
            throw new InvalidOperationException("Server admission lifecycle owner is unavailable.");
        var options = new SharpLinkAdmissionControlOptions();
        configure(options);
        options.Validate();
        return controller.Kernel.CreateProgram(options, _staticManifests);
    }

    internal AdmissionProgram? PublishAdmissionProgramForTests(AdmissionProgram? program)
    {
        var lifecycle = _admissionController ??
            throw new InvalidOperationException("Server admission lifecycle owner is unavailable.");
        if (program is not null && !ReferenceEquals(program.Kernel, lifecycle.Kernel))
            throw new InvalidOperationException("Admission program belongs to a different server state kernel.");
        if (program is { IsRetired: true })
            throw new InvalidOperationException("A retired admission program cannot be published again.");

        AdmissionProgram previous;
        lock (_registryGate)
        {
            if (CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
                program?.Retire();
                throw new InvalidOperationException("Admission publication is sealed because the server is stopping.");
            }

            var replacement = program ?? AdmissionProgram.Disabled;
            previous = ReadAdmissionPublication();
            if (ReferenceEquals(previous, replacement))
                return previous.IsEnabled ? previous : null;
            Volatile.Write(ref _admissionProgram, replacement);
            if (previous.IsEnabled)
                previous.Retire();
        }
        return previous.IsEnabled ? previous : null;
    }

    private AdmissionProgram? CaptureAdmissionProgram(long requestId)
    {
        while (true)
        {
            var publication = ReadAdmissionPublication();
            if (!publication.IsEnabled)
            {
                Volatile.Read(ref s_afterAdmissionCaptureForTests)?.Invoke(this, requestId, null);
                return null;
            }

            Volatile.Read(ref s_afterAdmissionPublicationReadForTests)?.Invoke(this, requestId, publication);
            if (!publication.TryAcquireUse())
            {
                // Shutdown retires every live program after the server state has been sealed. The
                // publication pointer may still name that retired object, but no admitted Request
                // may attach to it and there is no reason to spin once shutdown cancellation is live.
                if (_admissionController?.Kernel.IsDraining == true)
                    return null;
                continue;
            }

            try
            {
                Volatile.Read(ref s_afterAdmissionCaptureForTests)?.Invoke(this, requestId, publication);
                return publication;
            }
            catch
            {
                publication.ReleaseUse();
                throw;
            }
        }
    }

    private AdmissionProgram ReadAdmissionPublication()
    {
        var publication = Volatile.Read(ref _admissionProgram);
        if (!ReferenceEquals(publication, AdmissionProgram.Uninitialized))
            return publication;

        var initial = _admissionController?.Program ?? AdmissionProgram.Disabled;
        var observed = Interlocked.CompareExchange(
            ref _admissionProgram,
            initial,
            AdmissionProgram.Uninitialized);
        return ReferenceEquals(observed, AdmissionProgram.Uninitialized)
            ? initial
            : observed;
    }
}
