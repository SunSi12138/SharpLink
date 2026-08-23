namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private static Action<SharpLinkServer, long, AdmissionProgram?>? s_afterAdmissionCaptureForTests;

    private AdmissionProgram _admissionProgram = AdmissionProgram.Uninitialized;

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

    internal AdmissionProgram? OwnedAdmissionProgramForTests
        => _admissionController is null
            ? null
            : AdmissionProgram.FromController(_admissionController);

    internal AdmissionProgram? PublishAdmissionProgramForTests(AdmissionProgram? program)
    {
        var replacement = program ?? AdmissionProgram.Disabled;
        var previous = Interlocked.Exchange(ref _admissionProgram, replacement);
        if (ReferenceEquals(previous, AdmissionProgram.Uninitialized))
        {
            previous = _admissionController is null
                ? AdmissionProgram.Disabled
                : AdmissionProgram.FromController(_admissionController);
        }
        return previous.IsEnabled ? previous : null;
    }

    private AdmissionProgramUse? CaptureAdmissionProgram(
        long requestId,
        out AdmissionProgram? program)
    {
        var publication = ReadAdmissionPublication();
        program = publication.IsEnabled ? publication : null;
        var use = program?.AcquireUse();
        try
        {
            Volatile.Read(ref s_afterAdmissionCaptureForTests)?.Invoke(this, requestId, program);
            return use;
        }
        catch
        {
            use?.Dispose();
            throw;
        }
    }

    private AdmissionProgram ReadAdmissionPublication()
    {
        var publication = Volatile.Read(ref _admissionProgram);
        if (!ReferenceEquals(publication, AdmissionProgram.Uninitialized))
            return publication;

        var initial = _admissionController is null
            ? AdmissionProgram.Disabled
            : AdmissionProgram.FromController(_admissionController);
        var observed = Interlocked.CompareExchange(
            ref _admissionProgram,
            initial,
            AdmissionProgram.Uninitialized);
        return ReferenceEquals(observed, AdmissionProgram.Uninitialized)
            ? initial
            : observed;
    }
}
