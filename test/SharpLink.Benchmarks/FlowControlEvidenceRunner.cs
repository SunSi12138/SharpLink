using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Measures receive-state lifecycle allocation after growing the dictionary to the negotiated
/// maximum. The workload touches only receive credit APIs, so it cannot create send waiters.
/// </summary>
internal static class FlowControlEvidenceRunner
{
    private static readonly int[] SItemsPerStream = [1, 4, 64];
    private static readonly int[] SActiveStreams = [1, 8, 32, 128];

    internal static async Task RunAsync(string[] args)
    {
        var warmupBatches = GetPositiveOption(args, "--warmup-batches", 1_000);
        var measurementBatches = GetPositiveOption(args, "--measurement-batches", 10_000);
        var outputPath = GetOption(args, "--output") ?? Path.Combine(
            "artifacts",
            "performance",
            "current",
            "flow-control-receive-state.json");
        var results = new List<FlowControlEvidenceResult>(SItemsPerStream.Length * (SActiveStreams.Length + 1));

        Console.WriteLine(
            "Flow-control allocation evidence: dictionary prewarmed to 128 entries; " +
            "no send APIs, waiters, or connection-threshold flushes are exercised.");
        foreach (var itemsPerStream in SItemsPerStream)
        {
            foreach (var activeStreams in SActiveStreams)
            {
                var workload = new ReceiveFlowStateShortWorkload(itemsPerStream);
                results.Add(Measure(
                    scenario: "short-stream",
                    itemsPerStream,
                    activeStreams,
                    warmupBatches,
                    measurementBatches,
                    () => workload.Run(activeStreams)));
            }

            var longLived = new ReceiveFlowStateLongLivedWorkload(itemsPerStream);
            results.Add(Measure(
                scenario: "long-lived-control",
                itemsPerStream,
                activeStreams: 1,
                warmupBatches,
                measurementBatches,
                longLived.Run));
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true
        })).ConfigureAwait(false);
        Console.WriteLine($"Flow-control receive-state evidence: {fullPath}");
    }

    private static FlowControlEvidenceResult Measure(
        string scenario,
        int itemsPerStream,
        int activeStreams,
        int warmupBatches,
        int measurementBatches,
        Func<int> operation)
    {
        for (var batch = 0; batch < warmupBatches; batch++)
            _ = operation();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var checksum = 0L;
        for (var batch = 0; batch < measurementBatches; batch++)
            checksum += operation();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        GC.KeepAlive(checksum);

        var streams = checked((long)measurementBatches * activeStreams);
        var items = checked(streams * itemsPerStream);
        var result = new FlowControlEvidenceResult(
            scenario,
            itemsPerStream,
            activeStreams,
            warmupBatches,
            measurementBatches,
            allocated / (double)measurementBatches,
            allocated / (double)streams,
            allocated / (double)items,
            elapsed.TotalNanoseconds / measurementBatches,
            elapsed.TotalNanoseconds / streams,
            elapsed.TotalNanoseconds / items);
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "case={0} items={1} streams={2} B/batch={3:F2} B/stream={4:F2} B/item={5:F2} ns/stream={6:F2} ns/item={7:F2}",
            scenario,
            itemsPerStream,
            activeStreams,
            result.AllocatedBytesPerBatch,
            result.AllocatedBytesPerStream,
            result.AllocatedBytesPerItem,
            result.NanosecondsPerStream,
            result.NanosecondsPerItem));
        return result;
    }

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
        var value = GetOption(args, name);
        if (value is null)
            return defaultValue;
        var parsed = int.Parse(value, CultureInfo.InvariantCulture);
        return parsed > 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }
}

internal sealed record FlowControlEvidenceResult(
    string Scenario,
    int ItemsPerStream,
    int ActiveStreams,
    int WarmupBatches,
    int MeasurementBatches,
    double AllocatedBytesPerBatch,
    double AllocatedBytesPerStream,
    double AllocatedBytesPerItem,
    double NanosecondsPerBatch,
    double NanosecondsPerStream,
    double NanosecondsPerItem);

/// <summary>
/// A receive-only matrix. Prewarming fills and drains all 128 entries once, retaining dictionary
/// capacity while every measured batch creates fresh short-lived stream states.
/// </summary>
internal sealed class ReceiveFlowStateShortWorkload
{
    private const int EncodedBytes = 32;
    private const int PrewarmedStreamCount = 128;
    private readonly StreamFlowController _controller;
    private readonly long[] _requestIds = new long[PrewarmedStreamCount];
    private readonly int _itemsPerStream;

    internal ReceiveFlowStateShortWorkload(int itemsPerStream)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemsPerStream);
        _itemsPerStream = itemsPerStream;
        var streamWindow = checked(itemsPerStream * EncodedBytes);
        _controller = new StreamFlowController(
            streamWindow,
            connectionWindow: checked(streamWindow * PrewarmedStreamCount * 4),
            maxFramePayloadBytes: 4 * 1024 * 1024,
            maxConcurrentStreams: PrewarmedStreamCount);
        for (var index = 0; index < _requestIds.Length; index++)
            _requestIds[index] = index + 1;
        PrewarmDictionaryCapacity();
    }

    internal int Run(int activeStreams)
    {
        if ((uint)(activeStreams - 1) >= _requestIds.Length)
            throw new ArgumentOutOfRangeException(nameof(activeStreams));

        for (var streamIndex = 0; streamIndex < activeStreams; streamIndex++)
        {
            var requestId = _requestIds[streamIndex];
            for (var item = 0; item < _itemsPerStream; item++)
                _controller.AcceptReceived(requestId, streamId: 1, EncodedBytes);
        }

        var returnedCredit = 0;
        for (var streamIndex = 0; streamIndex < activeStreams; streamIndex++)
        {
            var requestId = _requestIds[streamIndex];
            for (var item = 0; item < _itemsPerStream; item++)
                returnedCredit += _controller.RecordConsumed(requestId, streamId: 1, EncodedBytes);
            returnedCredit += _controller.FlushConsumed(requestId, streamId: 1);
        }

        var expectedCredit = checked(activeStreams * _itemsPerStream * EncodedBytes);
        if (returnedCredit != expectedCredit)
            throw new InvalidOperationException("Receive-state workload did not return every reserved byte exactly once.");
        return returnedCredit;
    }

    private void PrewarmDictionaryCapacity()
    {
        for (var streamIndex = 0; streamIndex < _requestIds.Length; streamIndex++)
            _controller.AcceptReceived(_requestIds[streamIndex], streamId: 1, EncodedBytes);

        var returnedCredit = 0;
        for (var streamIndex = 0; streamIndex < _requestIds.Length; streamIndex++)
        {
            var requestId = _requestIds[streamIndex];
            returnedCredit += _controller.RecordConsumed(requestId, streamId: 1, EncodedBytes);
            returnedCredit += _controller.FlushConsumed(requestId, streamId: 1);
        }

        if (returnedCredit != PrewarmedStreamCount * EncodedBytes)
            throw new InvalidOperationException("Receive-state dictionary prewarm did not return every reserved byte.");
    }
}

/// <summary>Control workload that repeatedly uses one state created during setup.</summary>
internal sealed class ReceiveFlowStateLongLivedWorkload
{
    private const int EncodedBytes = 32;
    private readonly StreamFlowController _controller;
    private readonly int _itemsPerInvocation;

    internal ReceiveFlowStateLongLivedWorkload(int itemsPerInvocation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemsPerInvocation);
        _itemsPerInvocation = itemsPerInvocation;
        _controller = new StreamFlowController(
            streamWindow: EncodedBytes,
            connectionWindow: EncodedBytes * 4,
            maxFramePayloadBytes: 4 * 1024 * 1024,
            maxConcurrentStreams: 1);

        _controller.AcceptReceived(requestId: 1, streamId: 1, EncodedBytes);
        if (_controller.RecordConsumed(requestId: 1, streamId: 1, EncodedBytes) != EncodedBytes)
            throw new InvalidOperationException("Long-lived receive state could not be initialized.");
    }

    internal int Run()
    {
        var returnedCredit = 0;
        for (var item = 0; item < _itemsPerInvocation; item++)
        {
            _controller.AcceptReceived(requestId: 1, streamId: 1, EncodedBytes);
            returnedCredit += _controller.RecordConsumed(requestId: 1, streamId: 1, EncodedBytes);
        }

        var expectedCredit = checked(_itemsPerInvocation * EncodedBytes);
        if (returnedCredit != expectedCredit)
            throw new InvalidOperationException("Long-lived receive state did not return every reserved byte exactly once.");
        return returnedCredit;
    }
}
