namespace SharpLink.Runtime;

// Diagnostic-overlay-only instrumentation. Not present in either frozen product
// source or the timing builds. Atomic counters can change consumption/EOF timing.
internal static class WindowUpdateCountDiagnostic
{
    internal const string Schema = "typed-wu-count-diagnostic-v2";
    private static long _clientEntered, _clientAccepted, _clientHandlerCompleted;
    private static long _clientEnteredBytes, _clientAcceptedBytes, _clientReceivedCreditArgumentBytes;
    private static long _serverEntered, _serverAccepted, _serverHandlerCompleted;
    private static long _serverEnteredBytes, _serverAcceptedBytes, _serverReceivedCreditArgumentBytes;
    private static long _invalidCredits, _invalidRoles;

    internal static void Entered(RpcSessionRole role, int credit)
        => Add(role, credit, ref _clientEntered, ref _clientEnteredBytes, ref _serverEntered, ref _serverEnteredBytes);
    internal static void Accepted(RpcSessionRole role, int credit)
        => Add(role, credit, ref _clientAccepted, ref _clientAcceptedBytes, ref _serverAccepted, ref _serverAcceptedBytes);
    // Completion means the peer handler returned; it may discard or clamp credit.
    internal static void HandlerCompleted(RpcSessionRole role, int credit)
        => Add(role, credit, ref _clientHandlerCompleted, ref _clientReceivedCreditArgumentBytes, ref _serverHandlerCompleted, ref _serverReceivedCreditArgumentBytes);

    private static void Add(RpcSessionRole role, int credit, ref long clientCount, ref long clientBytes,
        ref long serverCount, ref long serverBytes)
    {
        if (credit <= 0) Interlocked.Increment(ref _invalidCredits);
        if (role == RpcSessionRole.Client)
        {
            Interlocked.Increment(ref clientCount);
            Interlocked.Add(ref clientBytes, credit);
        }
        else if (role == RpcSessionRole.Server)
        {
            Interlocked.Increment(ref serverCount);
            Interlocked.Add(ref serverBytes, credit);
        }
        else Interlocked.Increment(ref _invalidRoles);
    }

    // Read only outside the RPC loop, after owners and peer application quiesce.
    // No reset: each fixed cell uses a fresh process and an initially zero snapshot.
    internal static WindowUpdateCountSnapshot Snapshot() => new(
        new(Interlocked.Read(ref _clientEntered), Interlocked.Read(ref _clientAccepted), Interlocked.Read(ref _clientHandlerCompleted),
            Interlocked.Read(ref _clientEnteredBytes), Interlocked.Read(ref _clientAcceptedBytes), Interlocked.Read(ref _clientReceivedCreditArgumentBytes)),
        new(Interlocked.Read(ref _serverEntered), Interlocked.Read(ref _serverAccepted), Interlocked.Read(ref _serverHandlerCompleted),
            Interlocked.Read(ref _serverEnteredBytes), Interlocked.Read(ref _serverAcceptedBytes), Interlocked.Read(ref _serverReceivedCreditArgumentBytes)),
        Interlocked.Read(ref _invalidCredits), Interlocked.Read(ref _invalidRoles));
}

internal readonly record struct WindowUpdateCountSide(long Entered, long Accepted, long HandlerCompleted,
    long EnteredCreditArgumentBytes, long AcceptedCreditArgumentBytes, long ReceivedCreditArgumentBytes);
internal readonly record struct WindowUpdateCountSnapshot(WindowUpdateCountSide Client, WindowUpdateCountSide Server,
    long InvalidCredits, long InvalidRoles);
