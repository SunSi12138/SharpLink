using System;
using System.IO;
using System.Text.Json;

namespace SharpLink.UnitTests.Runtime;

// Diagnostic only: the CI evidence job runs this same source in pinned dev and the
// isolated candidate. This is NOT a correctness assertion or an RPC throughput test.
public sealed class StreamManagerLazyMapAllocationEvidence
{
    [Test]
    public void MeasureDefaultAndNamedRequestLifecycleAllocations()
    {
        var output = Environment.GetEnvironmentVariable("SHARPLINK_LAZY_ROUTE_EVIDENCE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
            return; // Ordinary UnitTests is a correctness suite; opt-in evidence runs separately.

        var dispatcher = new StatelessDispatcher();
        var defaultAlloc = Measure(0, dispatcher);
        var namedAlloc = Measure(7, dispatcher);
        var doc = new
        {
            Source = Environment.GetEnvironmentVariable("SHARPLINK_LAZY_ROUTE_EVIDENCE_SHA") ?? "unset",
            RevisionMode = Environment.GetEnvironmentVariable("SHARPLINK_LAZY_ROUTE_EVIDENCE_MODE") ?? "unset",
            TimestampUtc = DateTimeOffset.UtcNow,
            OperationsPerRepeat = 10000,
            WarmupOperations = 2048,
            DefaultBytesPerLifecycle = defaultAlloc,
            NamedBytesPerLifecycle = namedAlloc,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double[] Measure(ushort streamId, IStreamDispatcher dispatcher)
    {
        const long requestId = 7890123;
        var results = new double[3];
        for (var repeat = 0; repeat < results.Length; repeat++)
        {
            var manager = new StreamManager();
            for (var warmup = 0; warmup < 2048; warmup++)
            {
                manager.Register(requestId, streamId, dispatcher);
                manager.Unregister(requestId, streamId);
            }
            // Warmup is outside the counted region. The identical code executes in
            // both binaries; the test reuses the same stateless dispatcher.
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var operation = 0; operation < 10000; operation++)
            {
                manager.Register(requestId, streamId, dispatcher);
                manager.Unregister(requestId, streamId);
            }
            results[repeat] = (GC.GetAllocatedBytesForCurrentThread() - before) / 10000.0;
            manager.AssertAccountingInvariant();
        }
        return results;
    }

    private sealed class StatelessDispatcher : IStreamDispatcher
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
    }
}
