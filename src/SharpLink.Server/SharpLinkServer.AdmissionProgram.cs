namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private static Action<SharpLinkServer, long, AdmissionProgram?>? s_afterAdmissionCaptureForTests;

    private readonly AdmissionProgram? _ownedAdmissionProgram;
    private AdmissionProgram? _admissionProgram;

    internal static Action<SharpLinkServer, long, AdmissionProgram?>? AfterAdmissionCaptureForTests
    {
        get => Volatile.Read(ref s_afterAdmissionCaptureForTests);
        set => Volatile.Write(ref s_afterAdmissionCaptureForTests, value);
    }

    internal AdmissionProgram? CurrentAdmissionProgramForTests
        => Volatile.Read(ref _admissionProgram);

    internal AdmissionProgram? OwnedAdmissionProgramForTests => _ownedAdmissionProgram;

    internal AdmissionProgram? PublishAdmissionProgramForTests(AdmissionProgram? program)
        => Interlocked.Exchange(ref _admissionProgram, program);

    private AdmissionProgram? CaptureAdmissionProgram(long requestId)
    {
        var program = Volatile.Read(ref _admissionProgram);
        Volatile.Read(ref s_afterAdmissionCaptureForTests)?.Invoke(this, requestId, program);
        return program;
    }

    private void StopAdmissionPrograms()
    {
        var current = Volatile.Read(ref _admissionProgram);
        current?.Controller.StopAccepting();
        if (_ownedAdmissionProgram is not null && !ReferenceEquals(current, _ownedAdmissionProgram))
            _ownedAdmissionProgram.Controller.StopAccepting();
    }
}
