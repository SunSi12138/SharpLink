using System.Diagnostics.Tracing;

namespace SharpLink.Benchmarks;

// Diagnostic-only markers. No per-item event, payload or production path is added.
[EventSource(Name = "SharpLink-735-AllocationWindow")]
internal sealed class AllocationWindow735 : EventSource
{
    internal static readonly AllocationWindow735 Log = new();
    private AllocationWindow735() { }

    [Event(1, Level = EventLevel.Informational)]
    public void WindowBegin() => WriteEvent(1);

    [Event(2, Level = EventLevel.Informational)]
    public void WindowEnd(long operations, long items, long allocatedBytes)
        => WriteEvent(2, operations, items, allocatedBytes);
}
