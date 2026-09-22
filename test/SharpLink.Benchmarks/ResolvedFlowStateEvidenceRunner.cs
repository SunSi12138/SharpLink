using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Issue #735 microkernel for comparing key lookup against generation-bound resolved state leases.
/// The matrix deliberately includes short streams and very long streams; contention probes are
/// reported separately because task scheduling allocations are not comparable to the sequential
/// B/item measurements.
/// </summary>
internal static class ResolvedFlowStateEvidenceRunner
{
    private const int EncodedBytes = 16;
    private static readonly int[] SActiveStreams = [1, 8, 32, 128];
    private static readonly int[] SItemsPerStream = [1, 64, 1_000, 100_000];

    internal static async Task RunAsync(string[] args)
    {
        var repetitions = GetPositiveOption(args, "--repetitions", 3);
        var contentionItems = GetPositiveOption(args, "--contention-items", 10_000);
        var outputPath = GetOption(args, "--output") ?? Path.Combine(
            "artifacts",
            "performance",
            "current",
            "resolved-flow-state.json");
        var results = new List<ResolvedFlowStateEvidenceResult>();

        foreach (var activeStreams in SActiveStreams)
        {
            foreach (var itemsPerStream in SItemsPerStream)
            {
                foreach (var resolved in new[] { false, true })
                {
                    results.Add(Measure(
                        "send-no-wait",
                        resolved,
                        activeStreams,
                        itemsPerStream,
                        repetitions,
                        () => RunSendNoWait(activeStreams, itemsPerStream, resolved)));
                    results.Add(Measure(
                        "receive-accept",
                        resolved,
                        activeStreams,
                        itemsPerStream,
                        repetitions,
                        () => RunReceiveAccept(activeStreams, itemsPerStream, resolved)));
                    results.Add(Measure(
                        "receive-consume",
                        resolved,
                        activeStreams,
                        itemsPerStream,
                        repetitions,
                        () => RunReceiveConsume(activeStreams, itemsPerStream, resolved)));
                    results.Add(Measure(
                        "receive-pair",
                        resolved,
                        activeStreams,
                        itemsPerStream,
                        repetitions,
                        () => RunReceivePair(activeStreams, itemsPerStream, resolved)));
                    results.Add(Measure(
                        "short-stream-control",
                        resolved,
                        activeStreams,
                        itemsPerStream,
                        repetitions,
                        () => RunShortReceiveLifecycle(activeStreams, itemsPerStream, resolved)));
                }
            }
        }

        foreach (var activeStreams in new[] { 32, 128 })
        {
            foreach (var resolved in new[] { false, true })
            {
                results.Add(await MeasureContentionAsync(
                    "send-contention",
                    resolved,
                    activeStreams,
                    contentionItems,
                    () => RunSendContentionAsync(activeStreams, contentionItems, resolved))
                    .ConfigureAwait(false));
                results.Add(await MeasureContentionAsync(
                    "receive-pair-contention",
                    resolved,
                    activeStreams,
                    contentionItems,
                    () => RunReceiveContentionAsync(activeStreams, contentionItems, resolved))
                    .ConfigureAwait(false));
            }
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);
        Console.WriteLine($"Resolved flow-state evidence: {fullPath}");
    }

    private static ResolvedFlowStateEvidenceResult Measure(
        string scenario,
        bool resolved,
        int activeStreams,
        int itemsPerStream,
        int repetitions,
        Func<long> operation)
    {
        _ = operation();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        long checksum = 0;
        for (var repetition = 0; repetition < repetitions; repetition++)
            checksum = checked(checksum + operation());
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        GC.KeepAlive(checksum);

        var items = checked((long)repetitions * activeStreams * itemsPerStream);
        var result = new ResolvedFlowStateEvidenceResult(
            scenario,
            resolved ? "resolved" : "key",
            activeStreams,
            itemsPerStream,
            repetitions,
            allocated / (double)items,
            elapsed.TotalNanoseconds / items,
            checksum);
        Print(result);
        return result;
    }

    private static async Task<ResolvedFlowStateEvidenceResult> MeasureContentionAsync(
        string scenario,
        bool resolved,
        int activeStreams,
        int itemsPerStream,
        Func<Task<long>> operation)
    {
        _ = await operation().ConfigureAwait(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var checksum = await operation().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var items = checked((long)activeStreams * itemsPerStream);
        var result = new ResolvedFlowStateEvidenceResult(
            scenario,
            resolved ? "resolved" : "key",
            activeStreams,
            itemsPerStream,
            Repetitions: 1,
            allocated / (double)items,
            elapsed.TotalNanoseconds / items,
            checksum);
        Print(result);
        return result;
    }

    private static long RunSendNoWait(int activeStreams, int itemsPerStream, bool resolved)
    {
        var controller = CreateController(
            streamWindow: EncodedBytes,
            connectionWindow: checked(EncodedBytes * activeStreams));
        var leases = new StreamFlowController.ResolvedSendCreditLease[activeStreams];

        for (var stream = 0; stream < activeStreams; stream++)
        {
            var requestId = stream + 1L;
            if (!controller.TryAcquireSendCredit(requestId, 1, EncodedBytes))
                throw new InvalidOperationException("send setup failed");
            controller.ReturnUnsentCredit(requestId, 1, EncodedBytes);
            leases[stream] = controller.ResolveSendCreditLease(requestId, 1);
        }

        long checksum = 0;
        for (var item = 0; item < itemsPerStream; item++)
        {
            for (var stream = 0; stream < activeStreams; stream++)
            {
                var requestId = stream + 1L;
                var acquired = resolved
                    ? controller.TryAcquireSendCredit(in leases[stream], EncodedBytes)
                    : controller.TryAcquireSendCredit(requestId, 1, EncodedBytes);
                if (!acquired)
                    throw new InvalidOperationException("send fast path unexpectedly blocked");
                if (resolved)
                    controller.ReturnUnsentCredit(in leases[stream], EncodedBytes);
                else
                    controller.ReturnUnsentCredit(requestId, 1, EncodedBytes);
                checksum += EncodedBytes;
            }
        }
        return checksum;
    }

    private static long RunReceiveAccept(int activeStreams, int itemsPerStream, bool resolved)
    {
        var streamWindow = checked(EncodedBytes * itemsPerStream);
        var controller = CreateController(
            streamWindow,
            checked(streamWindow * activeStreams));
        var leases = ResolveReceiveLeases(controller, activeStreams);

        for (var item = 0; item < itemsPerStream; item++)
        {
            for (var stream = 0; stream < activeStreams; stream++)
            {
                if (resolved)
                    controller.AcceptReceived(in leases[stream], EncodedBytes);
                else
                    controller.AcceptReceived(stream + 1L, 1, EncodedBytes);
            }
        }

        for (var stream = 0; stream < activeStreams; stream++)
        {
            var bytes = checked(itemsPerStream * EncodedBytes);
            if (resolved)
            {
                _ = controller.RecordConsumed(in leases[stream], bytes);
                _ = controller.FlushConsumed(in leases[stream]);
            }
            else
            {
                _ = controller.RecordConsumed(stream + 1L, 1, bytes);
                _ = controller.FlushConsumed(stream + 1L, 1);
            }
        }
        return checked((long)activeStreams * itemsPerStream * EncodedBytes);
    }

    private static long RunReceiveConsume(int activeStreams, int itemsPerStream, bool resolved)
    {
        var streamWindow = checked(EncodedBytes * itemsPerStream);
        var controller = CreateController(
            streamWindow,
            checked(streamWindow * activeStreams));
        var leases = ResolveReceiveLeases(controller, activeStreams);

        for (var stream = 0; stream < activeStreams; stream++)
        {
            for (var item = 0; item < itemsPerStream; item++)
                controller.AcceptReceived(in leases[stream], EncodedBytes);
        }

        long checksum = 0;
        for (var item = 0; item < itemsPerStream; item++)
        {
            for (var stream = 0; stream < activeStreams; stream++)
            {
                checksum += resolved
                    ? controller.RecordConsumed(in leases[stream], EncodedBytes)
                    : controller.RecordConsumed(stream + 1L, 1, EncodedBytes);
            }
        }
        return checksum;
    }

    private static long RunReceivePair(int activeStreams, int itemsPerStream, bool resolved)
    {
        var controller = CreateController(
            streamWindow: EncodedBytes * 2,
            connectionWindow: checked(EncodedBytes * activeStreams * 2));
        var leases = ResolveReceiveLeases(controller, activeStreams);
        long checksum = 0;

        for (var item = 0; item < itemsPerStream; item++)
        {
            for (var stream = 0; stream < activeStreams; stream++)
            {
                if (resolved)
                {
                    controller.AcceptReceived(in leases[stream], EncodedBytes);
                    checksum += controller.RecordConsumed(in leases[stream], EncodedBytes);
                }
                else
                {
                    var requestId = stream + 1L;
                    controller.AcceptReceived(requestId, 1, EncodedBytes);
                    checksum += controller.RecordConsumed(requestId, 1, EncodedBytes);
                }
            }
        }
        return checksum;
    }

    private static long RunShortReceiveLifecycle(int activeStreams, int itemsPerStream, bool resolved)
    {
        var controller = CreateController(
            streamWindow: EncodedBytes,
            connectionWindow: checked(EncodedBytes * activeStreams));
        long checksum = 0;
        for (var item = 0; item < itemsPerStream; item++)
        {
            for (var stream = 0; stream < activeStreams; stream++)
            {
                var requestId = checked((long)item * activeStreams + stream + 1);
                if (resolved)
                {
                    var lease = controller.ResolveReceiveCreditLease(requestId, 1);
                    controller.AcceptReceived(in lease, EncodedBytes);
                    checksum += controller.RecordConsumed(in lease, EncodedBytes);
                    checksum += controller.FlushConsumed(in lease);
                }
                else
                {
                    controller.AcceptReceived(requestId, 1, EncodedBytes);
                    checksum += controller.RecordConsumed(requestId, 1, EncodedBytes);
                    checksum += controller.FlushConsumed(requestId, 1);
                }
            }
        }
        return checksum;
    }

    private static async Task<long> RunSendContentionAsync(
        int activeStreams,
        int itemsPerStream,
        bool resolved)
    {
        var controller = CreateController(
            streamWindow: EncodedBytes,
            connectionWindow: checked(EncodedBytes * activeStreams));
        var leases = new StreamFlowController.ResolvedSendCreditLease[activeStreams];
        for (var stream = 0; stream < activeStreams; stream++)
        {
            var requestId = stream + 1L;
            if (!controller.TryAcquireSendCredit(requestId, 1, EncodedBytes))
                throw new InvalidOperationException("contention setup failed");
            controller.ReturnUnsentCredit(requestId, 1, EncodedBytes);
            leases[stream] = controller.ResolveSendCreditLease(requestId, 1);
        }

        var tasks = new Task<long>[activeStreams];
        for (var stream = 0; stream < activeStreams; stream++)
        {
            var index = stream;
            tasks[index] = Task.Run(() =>
            {
                long checksum = 0;
                var requestId = index + 1L;
                var lease = leases[index];
                for (var item = 0; item < itemsPerStream; item++)
                {
                    var acquired = resolved
                        ? controller.TryAcquireSendCredit(in lease, EncodedBytes)
                        : controller.TryAcquireSendCredit(requestId, 1, EncodedBytes);
                    if (!acquired)
                        throw new InvalidOperationException("independent contention stream blocked");
                    if (resolved)
                        controller.ReturnUnsentCredit(in lease, EncodedBytes);
                    else
                        controller.ReturnUnsentCredit(requestId, 1, EncodedBytes);
                    checksum += EncodedBytes;
                }
                return checksum;
            });
        }

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        long total = 0;
        for (var index = 0; index < completed.Length; index++)
            total += completed[index];
        return total;
    }

    private static async Task<long> RunReceiveContentionAsync(
        int activeStreams,
        int itemsPerStream,
        bool resolved)
    {
        var controller = CreateController(
            streamWindow: EncodedBytes * 2,
            connectionWindow: checked(EncodedBytes * activeStreams * 2));
        var leases = ResolveReceiveLeases(controller, activeStreams);
        var tasks = new Task<long>[activeStreams];
        for (var stream = 0; stream < activeStreams; stream++)
        {
            var index = stream;
            tasks[index] = Task.Run(() =>
            {
                long checksum = 0;
                var requestId = index + 1L;
                var lease = leases[index];
                for (var item = 0; item < itemsPerStream; item++)
                {
                    if (resolved)
                    {
                        controller.AcceptReceived(in lease, EncodedBytes);
                        checksum += controller.RecordConsumed(in lease, EncodedBytes);
                    }
                    else
                    {
                        controller.AcceptReceived(requestId, 1, EncodedBytes);
                        checksum += controller.RecordConsumed(requestId, 1, EncodedBytes);
                    }
                }
                return checksum;
            });
        }

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        long total = 0;
        for (var index = 0; index < completed.Length; index++)
            total += completed[index];
        return total;
    }

    private static StreamFlowController.ResolvedReceiveCreditLease[] ResolveReceiveLeases(
        StreamFlowController controller,
        int activeStreams)
    {
        var leases = new StreamFlowController.ResolvedReceiveCreditLease[activeStreams];
        for (var stream = 0; stream < activeStreams; stream++)
            leases[stream] = controller.ResolveReceiveCreditLease(stream + 1L, 1);
        return leases;
    }

    private static StreamFlowController CreateController(int streamWindow, int connectionWindow)
        => new(
            streamWindow,
            connectionWindow,
            maxFramePayloadBytes: 4 * 1024 * 1024,
            maxConcurrentStreams: 128);

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        }
        return null;
    }

    private static int GetPositiveOption(string[] args, string name, int defaultValue)
    {
        var raw = GetOption(args, name);
        if (raw is null)
            return defaultValue;
        var parsed = int.Parse(raw, CultureInfo.InvariantCulture);
        return parsed > 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }

    private static void Print(ResolvedFlowStateEvidenceResult result)
    {
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "case={0} mode={1} c={2} items={3} B/item={4:F3} ns/item={5:F3} checksum={6}",
            result.Scenario,
            result.Mode,
            result.ActiveStreams,
            result.ItemsPerStream,
            result.AllocatedBytesPerItem,
            result.NanosecondsPerItem,
            result.Checksum));
    }
}

internal sealed record ResolvedFlowStateEvidenceResult(
    string Scenario,
    string Mode,
    int ActiveStreams,
    int ItemsPerStream,
    int Repetitions,
    double AllocatedBytesPerItem,
    double NanosecondsPerItem,
    long Checksum);
