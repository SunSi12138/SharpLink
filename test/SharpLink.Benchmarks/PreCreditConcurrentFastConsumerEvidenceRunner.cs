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
        {
            var unsizedCodec = new UnsizedPayloadCodec();
            await RunCaseAsync(
                producers,
                caseName: "unsized",
                new UnsizedPayload(PayloadBytes),
                unsizedCodec,
                () => unsizedCodec.SerializeCount).ConfigureAwait(false);

            await RunCaseAsync(
                producers,
                caseName: "exact",
                new ConcurrentSizedPayload(PayloadBytes),
                new ConcurrentSizedPayloadCodec(),
                serializeCount: null).ConfigureAwait(false);
        }
    }

    private static async Task RunCaseAsync<T>(
        int producers,
        string caseName,
        T item,
        IRpcCodec<T> codec,
        Func<int>? serializeCount)
    {
        using var context = CreateContext(codec);
        using var transport = new BenchmarkTransport($"pre-credit-fast-{caseName}-{producers}");
        await using var session = new RpcSession(
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
            throw new InvalidOperationException("Concurrent fast-consumer evidence handshake failed.");

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
            await RunPhaseAsync(
                session,
                producers,
                warmupOperationsPerProducer,
                item,
                samples: null).ConfigureAwait(false);
            await DrainSendQueueAsync(session).ConfigureAwait(false);

            var throughputs = new double[MeasuredRounds];
            var p50 = new double[MeasuredRounds];
            var p95 = new double[MeasuredRounds];
            var p99 = new double[MeasuredRounds];
            var maxima = new double[MeasuredRounds];
            for (var round = 0; round < MeasuredRounds; round++)
            {
                var samples = new long[checked(producers * measuredOperationsPerProducer)];
                var elapsed = Stopwatch.StartNew();
                await RunPhaseAsync(
                    session,
                    producers,
                    measuredOperationsPerProducer,
                    item,
                    samples).ConfigureAwait(false);
                elapsed.Stop();

                // The transport pump is deliberately outside the timed region, but every round
                // starts with an empty downstream queue. Otherwise high producer counts can turn
                // this pre-credit evidence into a send-queue saturation test and accumulate
                // backlog across rounds.
                await DrainSendQueueAsync(session).ConfigureAwait(false);

                Array.Sort(samples);
                throughputs[round] = samples.Length / elapsed.Elapsed.TotalSeconds;
                p50[round] = ToNanoseconds(Percentile(samples, 0.50));
                p95[round] = ToNanoseconds(Percentile(samples, 0.95));
                p99[round] = ToNanoseconds(Percentile(samples, 0.99));
                maxima[round] = ToNanoseconds(samples[^1]);
                Console.WriteLine(
                    $"[PreCreditConcurrentFastRound] case={caseName} producers={producers} round={round + 1} " +
                    $"throughputOpsPerSec={throughputs[round]:F0} " +
                    $"p50Ns={p50[round]:F1} p95Ns={p95[round]:F1} " +
                    $"p99Ns={p99[round]:F1} maxNs={maxima[round]:F1}");
            }

            Console.WriteLine(
                $"[PreCreditConcurrentFast] case={caseName} producers={producers} payloadBytes={PayloadBytes} " +
                $"rounds={MeasuredRounds} operationsPerRound={producers * measuredOperationsPerProducer} " +
                $"throughputOpsPerSec={Median(throughputs):F0} " +
                $"p50Ns={Median(p50):F1} p95Ns={Median(p95):F1} " +
                $"p99Ns={Median(p99):F1} maxNs={Median(maxima):F1} " +
                $"serializeCount={(serializeCount is null ? "n/a" : serializeCount().ToString())} " +
                $"serializerPermitLimit={ReadInternalNumber(session, "PreCreditSerializationPermitLimit")} " +
                $"reservedBytes={ReadInternalNumber(session, "PreCreditSerializedBytes")} " +
                $"waiterCount={ReadInternalNumber(session, "PreCreditSerializedWaiterCount")}");
        }
        finally
        {
            if (raisedMinimum)
                ThreadPool.SetMinThreads(originalWorkerThreads, originalCompletionPortThreads);
        }
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
            // This evidence is about pre-credit admission. Keep the downstream queue large enough
            // for one bounded measurement round; DrainSendQueueAsync empties it between rounds.
            options.FlowControl.MaxSendQueueBytes = 256 * 1024 * 1024;
            options.FlowControl.StreamReceiveWindowBytes = 16 * 1024 * 1024;
            options.FlowControl.ConnectionReceiveWindowBytes = 16 * 1024 * 1024;
        });
        return builder.Build(includeGeneratedAssemblyCatalog: false);
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
