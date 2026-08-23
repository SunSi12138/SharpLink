from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    target = Path(path)
    text = target.read_text()
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected one replacement, found {count}: {old[:100]!r}")
    target.write_text(text.replace(old, new, 1))


Path("src/SharpLink.Server/Admission/AdmissionProgram.cs").write_text("""namespace SharpLink.Server;

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
""")

replace_once(
    "src/SharpLink.Server/SharpLinkServer.AdmissionProgram.cs",
    """    private AdmissionProgramUse? CaptureAdmissionProgram(
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
""",
    """    private AdmissionProgram? CaptureAdmissionProgram(long requestId)
    {
        var publication = ReadAdmissionPublication();
        var program = publication.IsEnabled ? publication : null;
        program?.AcquireUse();
        try
        {
            Volatile.Read(ref s_afterAdmissionCaptureForTests)?.Invoke(this, requestId, program);
            return program;
        }
        catch
        {
            program?.ReleaseUse();
            throw;
        }
    }
""")

replace_once(
    "src/SharpLink.Server/SharpLinkServer.RequestLoop.cs",
    """                                        var admissionProgramUse = CaptureAdmissionProgram(
                                            requestId,
                                            out var admissionProgram);
""",
    """                                        var admissionProgram = CaptureAdmissionProgram(requestId);
""")
request_loop = Path("src/SharpLink.Server/SharpLinkServer.RequestLoop.cs")
text = request_loop.read_text()
old = """                                                admissionProgram,
                                                admissionProgramUse);"""
if text.count(old) != 2:
    raise SystemExit(f"RequestLoop: expected two captured dispatch arguments, found {text.count(old)}")
request_loop.write_text(text.replace(old, """                                                admissionProgram);"""))

replace_once(
    "src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs",
    """        AdmissionProgram? admissionProgram,
        AdmissionProgramUse? admissionProgramUse,
        ServerCallCancellationState? admittedCallState = null,
""",
    """        AdmissionProgram? admissionProgram,
        ServerCallCancellationState? admittedCallState = null,
""")
replace_once(
    "src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs",
    """    {
        if (admissionProgram is null && admissionProgramUse is not null)
            throw new InvalidOperationException("A captured admission use requires its program generation.");
        if (!admissionGranted && admissionProgram is not null && admissionProgramUse is null)
            throw new InvalidOperationException("An enabled captured admission generation requires one use token.");

        try
""",
    """    {
        var ownsAdmissionProgramUse = admissionProgram is not null && !admissionGranted;
        try
""")
replace_once(
    "src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs",
    """                admittedCallState.AttachAdmissionProgramUse(admissionProgramUse!);
                admissionProgramUse = null;
""",
    """                admittedCallState.AttachAdmissionProgramUse(admissionProgram);
                ownsAdmissionProgramUse = false;
""")
replace_once(
    "src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs",
    """                admissionProgram,
                admissionProgramUse: null,
                callState,
""",
    """                admissionProgram,
                callState,
""")
replace_once(
    "src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs",
    """        finally
        {
            admissionProgramUse?.Dispose();
        }
""",
    """        finally
        {
            if (ownsAdmissionProgramUse)
                admissionProgram!.ReleaseUse();
        }
""")
admission_dispatch = Path("src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs")
text = admission_dispatch.read_text()
extra = "                admissionProgramUse: null,\n"
if text.count(extra) != 1:
    raise SystemExit(f"AdmissionDispatch: expected one remaining named use argument, found {text.count(extra)}")
admission_dispatch.write_text(text.replace(extra, "", 1))

replace_once(
    "src/SharpLink.Server/SharpLinkServer.InvocationDispatch.cs",
    """        AdmissionProgram? admissionProgram,
        AdmissionProgramUse? admissionProgramUse,
        ServerCallCancellationState? admittedCallState = null,
""",
    """        AdmissionProgram? admissionProgram,
        ServerCallCancellationState? admittedCallState = null,
""")
replace_once(
    "src/SharpLink.Server/SharpLinkServer.InvocationDispatch.cs",
    """    {
        if (admissionProgram is null && admissionProgramUse is not null)
            throw new InvalidOperationException("A captured admission use requires its program generation.");
        if (!admissionGranted && admissionProgram is not null && admissionProgramUse is null)
            throw new InvalidOperationException("An enabled captured admission generation requires one use token.");

        try
""",
    """    {
        var ownsAdmissionProgramUse = admissionProgram is not null && !admissionGranted;
        try
""")
replace_once(
    "src/SharpLink.Server/SharpLinkServer.InvocationDispatch.cs",
    """                admittedCallState.AttachAdmissionProgramUse(admissionProgramUse!);
                admissionProgramUse = null;
""",
    """                admittedCallState.AttachAdmissionProgramUse(admissionProgram);
                ownsAdmissionProgramUse = false;
""")
replace_once(
    "src/SharpLink.Server/SharpLinkServer.InvocationDispatch.cs",
    """        finally
        {
            admissionProgramUse?.Dispose();
        }
""",
    """        finally
        {
            if (ownsAdmissionProgramUse)
                admissionProgram!.ReleaseUse();
        }
""")

replace_once(
    "src/SharpLink.Server/ServerCallCancellationState.cs",
    "    private AdmissionProgramUse? _admissionProgramUse;\n",
    "    private AdmissionProgram? _admissionProgramUse;\n")
replace_once(
    "src/SharpLink.Server/ServerCallCancellationState.cs",
    """    internal void AttachAdmissionProgramUse(AdmissionProgramUse admissionProgramUse)
    {
        ArgumentNullException.ThrowIfNull(admissionProgramUse);
        if (Interlocked.CompareExchange(ref _admissionProgramUse, admissionProgramUse, null) is not null)
            throw new InvalidOperationException("An admission program use is already attached to this call.");
    }
""",
    """    internal void AttachAdmissionProgramUse(AdmissionProgram admissionProgram)
    {
        ArgumentNullException.ThrowIfNull(admissionProgram);
        if (Interlocked.CompareExchange(ref _admissionProgramUse, admissionProgram, null) is not null)
            throw new InvalidOperationException("An admission program use is already attached to this call.");
    }
""")
replace_once(
    "src/SharpLink.Server/ServerCallCancellationState.cs",
    "        Interlocked.Exchange(ref _admissionProgramUse, null)?.Dispose();\n",
    "        Interlocked.Exchange(ref _admissionProgramUse, null)?.ReleaseUse();\n")

unit = Path("test/SharpLink.UnitTests/Server/SharpLinkServerInvocationTests.cs")
lines = unit.read_text().splitlines(keepends=True)
start = next(i for i, line in enumerate(lines) if "return (ValueTask)DispatchMethod.Invoke(Server," in line)
end = next(i for i in range(start, start + 32) if "])!;" in lines[i])
token = next(i for i in range(start, end + 1) if lines[i].strip() == "CancellationToken.None,")
nulls = []
i = token + 1
while lines[i].strip() == "null,":
    nulls.append(i)
    i += 1
if len(nulls) != 3:
    raise SystemExit(f"unit harness: expected three nulls before admissionGranted, found {len(nulls)}")
del lines[nulls[-1]]
unit.write_text("".join(lines))
