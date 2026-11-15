namespace SharpLink.Runtime;

// A diagnostic may miss a contested section; it must never become another lifetime owner.
internal sealed record NonBlockingDiagnostic<T>(string State, T? Value, string? FailureType = null) where T : class
{
    internal static NonBlockingDiagnostic<T> Available(T value) => new("available", value);
    internal static NonBlockingDiagnostic<T> Capture(Lock gate, Func<T> capture)
    {
        if (!gate.TryEnter()) return new("contended", null);
        try { return CaptureEntered(capture); }
        finally { gate.Exit(); }
    }
    internal static NonBlockingDiagnostic<T> Capture(object gate, Func<T> capture)
    {
        if (!Monitor.TryEnter(gate)) return new("contended", null);
        try { return CaptureEntered(capture); }
        finally { Monitor.Exit(gate); }
    }
    private static NonBlockingDiagnostic<T> CaptureEntered(Func<T> capture)
    {
        try { return Available(capture()); }
        catch (Exception failure) { return new("failed", null, failure.GetType().FullName); }
    }
}
