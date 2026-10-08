using System.Diagnostics.Tracing;

namespace SharpLink.Profiling742;

// Diagnostic-only. Link/copy identically into each benchmark project in disposable trees.
// No per-operation or per-item instrumentation.
[EventSource(Name = "SharpLink-742-ProfileWindow")]
internal sealed class ProfileWindow742 : EventSource
{
    internal static readonly ProfileWindow742 Log = new();
    private ProfileWindow742() { }
    [Event(1, Level = EventLevel.Informational)]
    public void WindowBegin(string workload) => WriteEvent(1, workload);
    [Event(2, Level = EventLevel.Informational)]
    public void WindowEnd(string workload, long operations, long items)
        => WriteEvent(2, workload, operations, items);
    [Event(3, Level = EventLevel.Informational)]
    public void AdmissionsClosed(string workload) => WriteEvent(3, workload);
}
