using System.Diagnostics;
using System.Text.Json;

namespace SharpLink.Benchmarks;

/// <summary>
/// Exercises cancellation at the actual persistent executor boundary where the bounded
/// channel is full and an additional request is waiting in ChannelWriter.WriteAsync.
/// The probe uses the same DecodeCaseRuntime/PersistentDecodeExecutor/PersistentDecodeWorkItem
/// and real reservation/retained/output leases as comparative strategy D.
/// </summary>
internal static class DecodeExecutorBlockedWriterCancellationEvidenceRunner
{
    private const int DefaultQueueCapacity = 8;
    private const int DefaultQuantumBytes = 64 * 1024;

    internal static async Task RunAsync(string[] args)
    {
        var outputPath = GetOption(args, "--output") ??
            Path.Combine("artifacts", "performance", "current", "phase0-decode-blocked-writer-cancel.json");
        var payloadSize = GetPayloadSize(args);
        var compressible = GetCompressibility(args);
        var queueCapacity = GetPositiveInt(args, "--queue-capacity", DefaultQueueCapacity);
        var fixture = DecodeExecutionPhase0EvidenceRunner.DecodeFixture.Create(payloadSize, compressible);
        var result = await MeasureAsync(fixture, queueCapacity);

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        Console.WriteLine($"Phase 0 actual-D blocked-writer cancellation evidence: {fullPath}");
        Console.WriteLine(
            $"PHASE0_BLOCKED_WRITER_CANCEL payload={payloadSize} compressible={compressible} " +
            $"queueCapacity={queueCapacity} publishedBeforeCancel={result.PublishedBeforeCancel} " +
            $"occupiedWhileBlocked={result.OccupiedCallsWhileBlocked} " +
            $"queueDepthWhileBlocked={result.DecodeQueueDepthWhileBlocked} " +
            $"blockedReservationReleased={result.BlockedReservationReleased} " +
            $"blockedRetainedLeaseReleased={result.BlockedRetainedLeaseReleased} " +
            $"blockedDecodedLeaseReleased={result.BlockedDecodedLeaseReleased} " +
            $"providerStarts={result.ProviderStarts} skippedQueued={result.SkippedQueuedWorkItems} " +
            $"cancelCompletionUs={result.BlockedCancellationCompletionMicroseconds:F2}");
    }

    private static async Task<BlockedWriterCancellationEvidenceResult> MeasureAsync(
        DecodeExecutionPhase0EvidenceRunner.DecodeFixture fixture,
        int queueCapacity)
    {
        var workerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queueFilled = new ManualResetEventSlim(false);
        var publishedCount = 0;
        var unexpectedCompletions = 0;
        var queuedCancellations = new CancellationTokenSource[queueCapacity];
        var queuedRequests = new Task[queueCapacity];
        using var blockedCancellation = new CancellationTokenSource();

        await using var runtime = new DecodeExecutionPhase0EvidenceRunner.DecodeCaseRuntime(
            fixture,
            DecodeExecutionPhase0EvidenceRunner.DecodeStrategy.PersistentExecutor,
            DecodeExecutionPhase0EvidenceRunner.AdmissionMode.Off,
            DecodeExecutionPhase0EvidenceRunner.CapacityMode.Available,
            queueCapacity + 1,
            DefaultQuantumBytes,
            executorQueueCapacity: queueCapacity,
            executorWorkerGate: workerGate.Task,
            onExecutorWorkPublished: () =>
            {
                if (Interlocked.Increment(ref publishedCount) == queueCapacity)
                    queueFilled.Set();
            });

        try
        {
            for (var index = 0; index < queuedRequests.Length; index++)
            {
                var cancellation = new CancellationTokenSource();
                queuedCancellations[index] = cancellation;
                queuedRequests[index] = RunRequestAsync(runtime, cancellation, () =>
                    Interlocked.Increment(ref unexpectedCompletions));
            }

            if (!queueFilled.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Actual-D blocked-writer probe did not fill the executor queue.");

            var fullCapacity = runtime.CaptureCapacitySnapshot();
            var fullMetrics = runtime.CaptureMetrics();
            if (Volatile.Read(ref publishedCount) != queueCapacity)
                throw new InvalidOperationException("Actual-D blocked-writer probe did not publish exactly one full queue.");
            if (fullCapacity.OccupiedCalls != queueCapacity)
                throw new InvalidOperationException("Actual-D blocked-writer probe did not hold the full queue's reservations.");
            if (fullMetrics.CurrentDecodeQueueDepth != queueCapacity)
                throw new InvalidOperationException("Actual-D blocked-writer probe did not fill the real D queue.");
            if (fullMetrics.CurrentRetainedBytes <= 0 || fullMetrics.CurrentDecodedBytes <= 0)
                throw new InvalidOperationException("Actual-D blocked-writer probe did not hold real pooled leases for the full queue.");
            if (fullMetrics.DecompressCalls != 0)
                throw new InvalidOperationException("Actual-D blocked-writer probe entered provider work while workers were gated.");

            var blockedRequest = RunRequestAsync(runtime, blockedCancellation, () =>
                Interlocked.Increment(ref unexpectedCompletions));

            await WaitUntilAsync(
                () =>
                {
                    var capacity = runtime.CaptureCapacitySnapshot();
                    var metrics = runtime.CaptureMetrics();
                    return capacity.OccupiedCalls == queueCapacity + 1 &&
                        metrics.CurrentDecodeQueueDepth == queueCapacity + 1 &&
                        metrics.CurrentRetainedBytes > fullMetrics.CurrentRetainedBytes &&
                        metrics.CurrentDecodedBytes > fullMetrics.CurrentDecodedBytes;
                },
                "The extra actual-D request did not reach the blocked-writer ownership state.");

            var blockedCapacity = runtime.CaptureCapacitySnapshot();
            var blockedMetrics = runtime.CaptureMetrics();
            if (Volatile.Read(ref publishedCount) != queueCapacity)
            {
                throw new InvalidOperationException(
                    "The extra actual-D request published despite a full channel and gated workers.");
            }
            if (blockedRequest.IsCompleted)
                throw new InvalidOperationException("The extra actual-D request completed before blocked-writer cancellation.");
            if (blockedMetrics.DecompressCalls != 0)
                throw new InvalidOperationException("The blocked actual-D writer entered provider work.");

            // With workers gated, all queueCapacity slots are already published and no reader can
            // free a slot. The ninth request has incremented D's queue-attempt metric and holds its
            // real reservation/retained/output leases, while the publish callback remains at eight;
            // it is therefore waiting before publication in the real ChannelWriter.WriteAsync path.
            var cancellationStarted = Stopwatch.GetTimestamp();
            blockedCancellation.Cancel();
            await blockedRequest.WaitAsync(TimeSpan.FromSeconds(5));
            var cancellationCompletionMicroseconds =
                Stopwatch.GetElapsedTime(cancellationStarted).TotalNanoseconds / 1000d;

            var afterBlockedCancelCapacity = runtime.CaptureCapacitySnapshot();
            var afterBlockedCancelMetrics = runtime.CaptureMetrics();
            var blockedReservationReleased = afterBlockedCancelCapacity.OccupiedCalls == queueCapacity;
            var blockedRetainedReleased =
                afterBlockedCancelMetrics.CurrentRetainedBytes == fullMetrics.CurrentRetainedBytes;
            var blockedDecodedReleased =
                afterBlockedCancelMetrics.CurrentDecodedBytes == fullMetrics.CurrentDecodedBytes;

            if (!blockedReservationReleased || !blockedRetainedReleased || !blockedDecodedReleased)
            {
                throw new InvalidOperationException(
                    "Cancelling the actual-D blocked writer did not restore reservation/retained/output ownership to the full-queue baseline.");
            }
            if (afterBlockedCancelMetrics.CurrentDecodeQueueDepth != queueCapacity)
            {
                throw new InvalidOperationException(
                    "Cancelling the actual-D blocked writer did not remove the unpublished enqueue attempt.");
            }
            if (Volatile.Read(ref publishedCount) != queueCapacity)
                throw new InvalidOperationException("The cancelled blocked writer was published into the actual D queue.");
            if (afterBlockedCancelMetrics.DecompressCalls != 0)
                throw new InvalidOperationException("The cancelled blocked writer entered provider work.");

            foreach (var cancellation in queuedCancellations)
                cancellation.Cancel();
            await Task.WhenAll(queuedRequests).WaitAsync(TimeSpan.FromSeconds(5));

            var beforeWorkerCapacity = runtime.CaptureCapacitySnapshot();
            var beforeWorkerMetrics = runtime.CaptureMetrics();
            if (beforeWorkerCapacity.OccupiedCalls != 0 ||
                beforeWorkerMetrics.CurrentRetainedBytes != 0 ||
                beforeWorkerMetrics.CurrentDecodedBytes != 0)
            {
                throw new InvalidOperationException(
                    "Actual-D blocked-writer probe did not release all caller ownership before worker service.");
            }
            if (beforeWorkerMetrics.CurrentDecodeQueueDepth != queueCapacity)
                throw new InvalidOperationException("Queued cancellation unexpectedly removed published items before worker release.");
            if (beforeWorkerMetrics.DecompressCalls != 0)
                throw new InvalidOperationException("Actual-D blocked-writer probe entered provider work before worker release.");

            workerGate.TrySetResult();
            await runtime.StopExecutorAsync();
            var afterDrainCapacity = runtime.CaptureCapacitySnapshot();
            var afterDrainMetrics = runtime.CaptureMetrics();
            if (Volatile.Read(ref unexpectedCompletions) != 0)
                throw new InvalidOperationException("Actual-D blocked-writer probe unexpectedly completed decode work.");
            if (afterDrainMetrics.DecompressCalls != 0)
                throw new InvalidOperationException("A cancelled actual-D request entered provider work after worker release.");
            if (afterDrainMetrics.SkippedCancelledWorkItems != queueCapacity)
                throw new InvalidOperationException("Actual D did not skip every published queued-cancelled work item.");
            if (afterDrainMetrics.CurrentDecodeQueueDepth != 0)
                throw new InvalidOperationException("Actual-D blocked-writer probe left queue attempts after drain.");
            if (afterDrainCapacity.OccupiedCalls != 0 ||
                afterDrainMetrics.CurrentRetainedBytes != 0 ||
                afterDrainMetrics.CurrentDecodedBytes != 0)
            {
                throw new InvalidOperationException("Actual-D blocked-writer probe leaked ownership after drain.");
            }

            return new BlockedWriterCancellationEvidenceResult(
                DateTimeOffset.UtcNow,
                fixture.PayloadSize,
                fixture.Compressible,
                fixture.Compressed.Length,
                queueCapacity,
                Volatile.Read(ref publishedCount),
                blockedCapacity.OccupiedCalls,
                blockedMetrics.CurrentDecodeQueueDepth,
                blockedReservationReleased,
                blockedRetainedReleased,
                blockedDecodedReleased,
                afterDrainMetrics.DecompressCalls,
                afterDrainMetrics.SkippedCancelledWorkItems,
                cancellationCompletionMicroseconds);
        }
        finally
        {
            workerGate.TrySetResult();
            blockedCancellation.Cancel();
            foreach (var cancellation in queuedCancellations)
            {
                if (cancellation is null)
                    continue;
                cancellation.Cancel();
                cancellation.Dispose();
            }
        }
    }

    private static Task RunRequestAsync(
        DecodeExecutionPhase0EvidenceRunner.DecodeCaseRuntime runtime,
        CancellationTokenSource cancellation,
        Action onUnexpectedCompletion)
        => Task.Run(async () =>
        {
            try
            {
                _ = await runtime.ExecuteAsync(cancellation.Token);
                onUnexpectedCompletion();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        });

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 5d);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException(failureMessage);
            await Task.Delay(1);
        }
    }

    private static int GetPayloadSize(string[] args)
    {
        var option = GetOption(args, "--payload-size");
        if (!int.TryParse(option, out var payloadSize) ||
            payloadSize is not (1024 or 65_536 or 1_048_576))
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "Payload size must be 1024, 65536, or 1048576.");
        }
        return payloadSize;
    }

    private static bool GetCompressibility(string[] args)
        => GetOption(args, "--compressibility")?.ToLowerInvariant() switch
        {
            "high" => true,
            "low" => false,
            _ => throw new ArgumentOutOfRangeException(
                nameof(args),
                "Compressibility must be high or low.")
        };

    private static int GetPositiveInt(string[] args, string name, int defaultValue)
    {
        var option = GetOption(args, name);
        if (option is null)
            return defaultValue;
        if (!int.TryParse(option, out var value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, "Expected a positive integer.");
        return value;
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
}

internal sealed record BlockedWriterCancellationEvidenceResult(
    DateTimeOffset CapturedAtUtc,
    int PayloadSize,
    bool Compressible,
    int CompressedBytes,
    int QueueCapacity,
    long PublishedBeforeCancel,
    long OccupiedCallsWhileBlocked,
    long DecodeQueueDepthWhileBlocked,
    bool BlockedReservationReleased,
    bool BlockedRetainedLeaseReleased,
    bool BlockedDecodedLeaseReleased,
    long ProviderStarts,
    long SkippedQueuedWorkItems,
    double BlockedCancellationCompletionMicroseconds);
