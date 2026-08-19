using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class PreCreditConcurrentFastConsumerEvidenceRunner
{
    private const int PayloadBytes = 1024;
    private const int TargetWarmupOperationsPerRound = 8_000;
    private const int MinimumWarmupOperationsPerProducer = 64;
    private const int TargetMeasuredOperationsPerRound = 40_000;
    private const int MinimumMeasuredOperationsPerProducer = 256;
    private const int MeasuredRounds = 5;
    private static readonly int[] ProducerCounts = [1, 8, 32, 128];

    internal static async Task RunAsync()
    {
        foreach (var producers in ProducerCounts)
            await RunPairedCaseAsync(producers).ConfigureAwait(false);
    }

    private static async Task RunPairedCaseAsync(int producers)
    {
        var unsizedCodec = new UnsizedPayloadCodec();
        using var unsizedContext = CreateContext(unsizedCodec);
        using var unsizedTransport = new BenchmarkTransport($"pre-credit-fast-unsized-{producers}");
        await using var unsizedSession = CreateReadySession(
            unsizedTransport,
            unsizedContext);

        var exactCodec = new ConcurrentSizedPayloadCodec();
        using var exactContext = CreateContext(exactCodec);
        using var exactTransport = new BenchmarkTransport($"pre-credit-fast-exact-{producers}");
        await using var exactSession = CreateReadySession(
            exactTransport,
            exactContext);

        var unsizedItem = new UnsizedPayload(PayloadBytes);
        var exactItem = new ConcurrentSizedPayload(PayloadBytes);
        var warmupOperationsPerProducer = Math.Max(
            MinimumWarmupOperationsPerProducer,
            TargetWarmupOperationsPerRound / producers);
        var measuredOperationsPerProducer = Math.Max(
            MinimumMeasuredOperationsPerProducer,
            TargetMeasuredOperationsPerRound / producers);

        ThreadPool.GetMinThreads(out var originalWorkerThreads, out var originalCompletionPortThreads);
        var raisedMinimum = originalWorkerThreads < producers &&
            ThreadPool.SetMinThreads(producers, originalCompletionPortThreads);
        try
        {
            // ABBA warmup gives both paths comparable JIT/pool/thread-pool preparation before
            // measurement and avoids making the second case systematically warmer.
            await WarmupAsync(
                unsizedSession,
                producers,
                warmupOperationsPerProducer,
                unsizedItem).ConfigureAwait(false);
            await WarmupAsync(
                exactSession,
                producers,
                warmupOperationsPerProducer,
                exactItem).ConfigureAwait(false);
            await WarmupAsync(
                exactSession,
                producers,
                warmupOperationsPerProducer,
                exactItem).ConfigureAwait(false);
            await WarmupAsync(
                unsizedSession,
                producers,
                warmupOperationsPerProducer,
                unsizedItem).ConfigureAwait(false);

            var unsizedRounds = new RoundMetrics[MeasuredRounds];
            var exactRounds = new RoundMetrics[MeasuredRounds];
            var throughputRatios = new double[MeasuredRounds];
            var p50Ratios = new double[MeasuredRounds];
            var p95Ratios = new double[MeasuredRounds];
            var p99Ratios = new double[MeasuredRounds];

            for (var round = 0; round < MeasuredRounds; round++)
            {
                var unsizedFirst = (round & 1) == 0;
                if (unsizedFirst)
                {
                    unsizedRounds[round] = await MeasureRoundAsync(
                        unsizedSession,
                        producers,
                        measuredOperationsPerProducer,
                        unsizedItem).ConfigureAwait(false);
                    exactRounds[round] = await MeasureRoundAsync(
                        exactSession,
                        producers,
                        measuredOperationsPerProducer,
                        exactItem).ConfigureAwait(false);
                }
                else
                {
                    exactRounds[round] = await MeasureRoundAsync(
                        exactSession,
                        producers,
                        measuredOperationsPerProducer,
                        exactItem).ConfigureAwait(false);
                    unsizedRounds[round] = await MeasureRoundAsync(
                        unsizedSession,
                        producers,
                        measuredOperationsPerProducer,
                        unsizedItem).ConfigureAwait(false);
                }

                throughputRatios[round] =
                    unsizedRounds[round].ThroughputOpsPerSec / exactRounds[round].ThroughputOpsPerSec;
                p50Ratios[round] = unsizedRounds[round].P50Ns / exactRounds[round].P50Ns;
                p95Ratios[round] = unsizedRounds[round].P95Ns / exactRounds[round].P95Ns;
                p99Ratios[round] = unsizedRounds[round].P99Ns / exactRounds[round].P99Ns;

                PrintCaseRound("unsized", producers, round + 1, unsizedRounds[round]);
                PrintCaseRound("exact", producers, round + 1, exactRounds[round]);
                Console.WriteLine(
                    $"[PreCreditConcurrentFastPairRound] producers={producers} pairRound={round + 1} " +
                    $"order={(unsizedFirst ? "unsized-exact" : "exact-unsized")} " +
                    $"throughputRatio={throughputRatios[round]:F6} " +
                    $"p50Ratio={p50Ratios[round]:F6} p95Ratio={p95Ratios[round]:F6} " +
                    $"p99Ratio={p99Ratios[round]:F6}");
            }

            PrintCaseSummary(
                "unsized",
                producers,
                measuredOperationsPerProducer,
                unsizedRounds,
                unsizedCodec.SerializeCount,
                unsizedSession);
            PrintCaseSummary(
                "exact",
                producers,
                measuredOperationsPerProducer,
                exactRounds,
                serializeCount: null,
                exactSession);

            Console.WriteLine(
                $"[PreCreditConcurrentFastPaired] producers={producers} payloadBytes={PayloadBytes} " +
                $"rounds={MeasuredRounds} operationsPerRound={producers * measuredOperationsPerProducer} " +
                $"medianThroughputRatio={Median(throughputRatios):F6} " +
                $"medianP50Ratio={Median(p50Ratios):F6} " +
                $"medianP95Ratio={Median(p95Ratios):F6} " +
                $"medianP99Ratio={Median(p99Ratios):F6}");
        }
        finally
        {
            if (raisedMinimum)
                ThreadPool.SetMinThreads(originalWorkerThreads, originalCompletionPortThreads);
        }
    }

    private static RpcSession CreateReadySession(
        BenchmarkTransport transport,
        SharpLinkRuntimeContext context)
    {
        var session = new RpcSession(
            transport,
            new RpcSessionCreationOptions(RpcSessionRole.Client, context));
        var negotiated = new NegotiatedSessionOptions(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.FlowControl,
            context.Protocol.MaxFramePayloadBytes,
            16 * 1024 * 1024,
            16 * 1024 * 1024,
            null);
        if (!session.TryCompleteHandshake(negotiated))
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("Concurrent fast-consumer evidence handshake failed.");
        }
        return session;
    }

    private static async Task WarmupAsync<T>(
        RpcSession session,
        int producers,
        int operationsPerProducer,
        T item)
    {
        await RunPhaseAsync(
            session,
            producers,
            operationsPerProducer,
            item,
            samples: null).ConfigureAwait(false);
        await DrainSendQueueAsync(session).ConfigureAwait(false);
    }

    private static async Task<RoundMetrics> MeasureRoundAsync<T>(
        RpcSession session,
        int producers,
        int operationsPerProducer,
        T item)
    {
        var samples = new long[checked(producers * operationsPerProducer)];
        var elapsed = Stopwatch.StartNew();
        await RunPhaseAsync(
            session,
            producers,
            operationsPerProducer,
            item,
            samples).ConfigureAwait(false);
        elapsed.Stop();

        // Keep downstream transport work out of the timed region while guaranteeing that the
        // next adjacent control measurement starts with an empty SendPump queue.
        await DrainSendQueueAsync(session).ConfigureAwait(false);

        Array.Sort(samples);
        return new RoundMetrics(
            samples.Length / elapsed.Elapsed.TotalSeconds,
            ToNanoseconds(Percentile(samples, 0.50)),
            ToNanoseconds(Percentile(samples, 0.95)),
            ToNanoseconds(Percentile(samples, 0.99)),
            ToNanoseconds(samples[^1]));
    }

    private static async Task RunPhaseAsync<T>(
        RpcSession session,
        int producers,
        int operationsPerProducer,
        T item,
        long[]? samples)
    {
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Task[producers];
        for (var producer = 0; producer < producers; producer++)
        {
            var requestId = producer + 1L;
            var sampleOffset = producer * operationsPerProducer;
            workers[producer] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                for (var operation = 0; operation < operationsPerProducer; operation++)
                {
                    var started = Stopwatch.GetTimestamp();
                    await session.SendStreamChunkAsync(
                        requestId,
                        1,
                        item,
                        CancellationToken.None).ConfigureAwait(false);
                    if (samples is not null)
                        samples[sampleOffset + operation] = Stopwatch.GetTimestamp() - started;

                    // The peer is a fast consumer: return both stream and connection credit
                    // immediately after each item so protocol credit never becomes the limiter.
                    session.ApplyWindowUpdate(
                        requestId,
                        new ProtocolV2WindowUpdate(1, PayloadBytes));
                }
            });
        }

        start.TrySetResult(true);
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private static async Task DrainSendQueueAsync(RpcSession session)
        => await session.FlushSendQueueAsync(CancellationToken.None).ConfigureAwait(false);

    private static SharpLinkRuntimeContext CreateContext<T>(IRpcCodec<T> codec)
    {
        var builder = new SharpLinkRuntimeContextBuilder()
            .AddCodec(codec);
        builder.Configure(options =>
        {
            // This evidence is about pre-credit admission. Keep one bounded measurement round
            // below SendPump capacity and drain it before the adjacent paired control.
            options.FlowControl.MaxSendQueueBytes = 256 * 1024 * 1024;
            options.FlowControl.StreamReceiveWindowBytes = 16 * 1024 * 1024;
            options.FlowControl.ConnectionReceiveWindowBytes = 16 * 1024 * 1024;
        });
        return builder.Build(includeGeneratedAssemblyCatalog: false);
    }

    private static void PrintCaseRound(
        string caseName,
        int producers,
        int round,
        RoundMetrics metrics)
    {
        Console.WriteLine(
            $"[PreCreditConcurrentFastRound] case={caseName} producers={producers} round={round} " +
            $"throughputOpsPerSec={metrics.ThroughputOpsPerSec:F0} " +
            $"p50Ns={metrics.P50Ns:F1} p95Ns={metrics.P95Ns:F1} " +
            $"p99Ns={metrics.P99Ns:F1} maxNs={metrics.MaxNs:F1}");
    }

    private static void PrintCaseSummary(
        string caseName,
        int producers,
        int operationsPerProducer,
        RoundMetrics[] rounds,
        int? serializeCount,
        RpcSession session)
    {
        var throughputs = new double[rounds.Length];
        var p50 = new double[rounds.Length];
        var p95 = new double[rounds.Length];
        var p99 = new double[rounds.Length];
        var maxima = new double[rounds.Length];
        for (var index = 0; index < rounds.Length; index++)
        {
            throughputs[index] = rounds[index].ThroughputOpsPerSec;
            p50[index] = rounds[index].P50Ns;
            p95[index] = rounds[index].P95Ns;
            p99[index] = rounds[index].P99Ns;
            maxima[index] = rounds[index].MaxNs;
        }

        Console.WriteLine(
            $"[PreCreditConcurrentFast] case={caseName} producers={producers} payloadBytes={PayloadBytes} " +
            $"rounds={rounds.Length} operationsPerRound={producers * operationsPerProducer} " +
            $"throughputOpsPerSec={Median(throughputs):F0} " +
            $"p50Ns={Median(p50):F1} p95Ns={Median(p95):F1} " +
            $"p99Ns={Median(p99):F1} maxNs={Median(maxima):F1} " +
            $"serializeCount={(serializeCount is null ? "n/a" : serializeCount.Value.ToString())} " +
            $"serializerPermitLimit={ReadInternalNumber(session, "PreCreditSerializationPermitLimit")} " +
            $"reservedBytes={ReadInternalNumber(session, "PreCreditSerializedBytes")} " +
            $"waiterCount={ReadInternalNumber(session, "PreCreditSerializedWaiterCount")}");
    }

    private static long Percentile(long[] sortedSamples, double percentile)
    {
        var index = (int)Math.Ceiling(sortedSamples.Length * percentile) - 1;
        return sortedSamples[Math.Clamp(index, 0, sortedSamples.Length - 1)];
    }

    private static double Median(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    private static double ToNanoseconds(long stopwatchTicks)
        => stopwatchTicks * (1_000_000_000d / Stopwatch.Frequency);

    private static string ReadInternalNumber(RpcSession session, string propertyName)
    {
        var property = typeof(RpcSession).GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        return property?.GetValue(session)?.ToString() ?? "n/a";
    }

    private readonly record struct RoundMetrics(
        double ThroughputOpsPerSec,
        double P50Ns,
        double P95Ns,
        double P99Ns,
        double MaxNs);

    private readonly record struct ConcurrentSizedPayload(int Bytes);

    private sealed class ConcurrentSizedPayloadCodec :
        IRpcCodec<ConcurrentSizedPayload>,
        IRpcSizedCodec<ConcurrentSizedPayload>
    {
        public bool CanExactSize => true;

        public void Serialize(in ConcurrentSizedPayload value, IBufferWriter<byte> buffer)
            => Write(value.Bytes, buffer);

        public ConcurrentSizedPayload Deserialize(in ReadOnlySequence<byte> buffer)
            => new(checked((int)buffer.Length));

        public bool TryGetEncodedSize(in ConcurrentSizedPayload value, out int size)
        {
            size = value.Bytes;
            return true;
        }

        public bool TryGetEncodedSize(
            in ConcurrentSizedPayload value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = value.Bytes;
            snapshot = null;
            return true;
        }

        public void SerializeSized(
            in ConcurrentSizedPayload value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
        {
            if (size != value.Bytes || snapshot is not null)
                throw new InvalidOperationException("Concurrent exact-size control received an invalid contract.");
            Write(value.Bytes, buffer);
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
            if (snapshot is not null)
                throw new InvalidOperationException("Concurrent exact-size control does not own snapshots.");
        }

        private static void Write(int bytes, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(bytes);
            span[..bytes].Fill(0x44);
            buffer.Advance(bytes);
        }
    }
}
