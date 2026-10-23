using System;
using System.Diagnostics;
using System.Threading;

namespace SharpLink.Benchmarks;

// Process-wide, non-atomic context snapshots; thread count means ThreadPool threads.
// Their timestamp span includes measurement-boundary overhead, not just RPC execution.
internal readonly record struct AllocationSampleDiagnostics(
    DateTimeOffset TimestampUtc,
    long Timestamp,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ThreadCount,
    long CompletedWorkItems,
    long PendingWorkItems)
{
    internal static AllocationSampleDiagnostics Capture() => new(
        DateTimeOffset.UtcNow,
        Stopwatch.GetTimestamp(),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        ThreadPool.ThreadCount,
        ThreadPool.CompletedWorkItemCount,
        ThreadPool.PendingWorkItemCount);
}
