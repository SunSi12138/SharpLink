using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using SharpLink.Runtime;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.Benchmarks;

// Candidate-internal attribution only. This is not generated-RPC E2E and does
// not silently replace the existing keyed/resolved production evidence matrix.
internal static class FirstReceiveAdmissionEvidence
{
    private const int EncodedBytes = 16;
    private enum Mode { Keyed, SeparateResolved, FusedFirst }
    private static readonly Mode[][] Orders =
    [
        [Mode.Keyed, Mode.SeparateResolved, Mode.FusedFirst],
        [Mode.FusedFirst, Mode.SeparateResolved, Mode.Keyed],
        [Mode.SeparateResolved, Mode.FusedFirst, Mode.Keyed],
        [Mode.Keyed, Mode.FusedFirst, Mode.SeparateResolved],
        [Mode.FusedFirst, Mode.Keyed, Mode.SeparateResolved],
        [Mode.SeparateResolved, Mode.Keyed, Mode.FusedFirst]
    ];

    public static async Task Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            await FirstReceiveAdmissionChecks.RunAllAsync();
            return;
        }
        if (args.Length != 1)
            throw new ArgumentException("Provide a CSV output path or --self-test.");
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var writer = new StreamWriter(output);
        writer.WriteLine("mode,active_streams,items_per_stream,repeat,items,ns_per_item,allocated_bytes_per_item,checksum");
        foreach (var activeStreams in new[] { 1, 32 })
        {
            foreach (var itemsPerStream in new[] { 1, 64 })
            {
                // Both receive windows, the full lifecycle and total item population are
                // identical in all modes. All streams remain live until the group flush.
                var controller = new StreamFlowController(EncodedBytes * 2,
                    EncodedBytes * 2 * activeStreams, 1024, activeStreams);
                var leases = new StreamFlowController.ResolvedReceiveCreditLease[activeStreams];
                var requestIds = new long[activeStreams];
                long nextRequestId = 0;
                var warmupGroups = Math.Max(1, 131072 / (activeStreams * itemsPerStream));
                var groups = Math.Max(1, 262144 / (activeStreams * itemsPerStream));
                foreach (var mode in Orders[0])
                    _ = Run(controller, leases, requestIds, mode, itemsPerStream, warmupGroups, ref nextRequestId);

                for (var repeat = 0; repeat < Orders.Length; repeat++)
                {
                    foreach (var mode in Orders[repeat])
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                        var started = Stopwatch.GetTimestamp();
                        var checksum = Run(controller, leases, requestIds, mode, itemsPerStream,
                            groups, ref nextRequestId);
                        var elapsed = Stopwatch.GetElapsedTime(started);
                        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                        var items = checked((long)groups * activeStreams * itemsPerStream);
                        if (checksum != items * EncodedBytes)
                            throw new InvalidOperationException("The measured lifecycle lost or invented receive credit.");
                        var row = string.Create(CultureInfo.InvariantCulture,
                            $"{mode},{activeStreams},{itemsPerStream},{repeat},{items},{elapsed.TotalNanoseconds / items:F6},{allocated / (double)items:F9},{checksum}");
                        writer.WriteLine(row);
                        Console.WriteLine(row);
                    }
                }
                controller.Complete(new OperationCanceledException("measurement complete"));
            }
        }
        Console.WriteLine($"First-receive mechanism evidence: {output}");
    }

    private static long Run(StreamFlowController controller,
        StreamFlowController.ResolvedReceiveCreditLease[] leases, long[] requestIds,
        Mode mode, int itemsPerStream, int groups, ref long nextRequestId)
    {
        long checksum = 0;
        for (var group = 0; group < groups; group++)
        {
            for (var stream = 0; stream < requestIds.Length; stream++)
                requestIds[stream] = ++nextRequestId;
            for (var item = 0; item < itemsPerStream; item++)
            {
                for (var stream = 0; stream < requestIds.Length; stream++)
                {
                    if (mode == Mode.Keyed)
                    {
                        controller.AcceptReceived(requestIds[stream], 1, EncodedBytes);
                        checksum += controller.RecordConsumed(requestIds[stream], 1, EncodedBytes);
                        continue;
                    }
                    if (item == 0)
                    {
                        if (mode == Mode.FusedFirst)
                        {
                            leases[stream] = controller.AcceptReceivedCreditLease(requestIds[stream], 1, EncodedBytes);
                        }
                        else
                        {
                            leases[stream] = controller.ResolveReceiveCreditLease(requestIds[stream], 1);
                            controller.AcceptReceived(in leases[stream], EncodedBytes);
                        }
                    }
                    else
                    {
                        controller.AcceptReceived(in leases[stream], EncodedBytes);
                    }
                    checksum += controller.RecordConsumed(in leases[stream], EncodedBytes);
                }
            }
            for (var stream = 0; stream < requestIds.Length; stream++)
            {
                checksum += mode == Mode.Keyed
                    ? controller.FlushConsumed(requestIds[stream], 1)
                    : controller.FlushConsumed(in leases[stream]);
            }
        }
        return checksum;
    }
}
