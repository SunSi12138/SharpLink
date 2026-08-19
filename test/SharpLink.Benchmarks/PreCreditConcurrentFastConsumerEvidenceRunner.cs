using System.Diagnostics;
using System.Reflection;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class PreCreditConcurrentFastConsumerEvidenceRunner
{
    private const int PayloadBytes = 1024;
    private const int WarmupOperationsPerProducer = 1_000;
    private const int MeasuredOperationsPerProducer = 5_000;
    private const int MeasuredRounds = 5;
    private static readonly int[] ProducerCounts = [1, 8, 32, 128];

    internal static async Task RunAsync()
    {
        foreach (var producers in ProducerCounts)
            await RunCaseAsync(producers).ConfigureAwait(false);
    }

    private static async Task RunCaseAsync(int producers)
    {
        var codec = new UnsizedPayloadCodec();
        using var context = CreateContext(codec);
        using var transport = new BenchmarkTransport($"pre-credit-fast-{producers}");
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

        ThreadPool.GetMinThreads(out var originalWorkerThreads, out var originalCompletionPortThreads);
        var raisedMinimum = originalWorkerThreads < producers &&
            ThreadPool.SetMinThreads(producers, originalCompletionPortThreads);
        try
        {
            await RunPhaseAsync(
                session,
                producers,
                WarmupOperationsPerProducer,
                samples: null).ConfigureAwait(false);

            var throughputs = new double[MeasuredRounds];
            var p50 = new double[MeasuredRounds];
            var p95 = new double[MeasuredRounds];
            var p99 = new double[MeasuredRounds];
            var maxima = new double[MeasuredRounds];
            for (var round = 0; round < MeasuredRounds; round++)
            {
                var samples = new long[checked(producers * MeasuredOperationsPerProducer)];
                var elapsed = Stopwatch.StartNew();
                await RunPhaseAsync(
                    session,
                    producers,
                    MeasuredOperationsPerProducer,
                    samples).ConfigureAwait(false);
                elapsed.Stop();

                Array.Sort(samples);
                throughputs[round] = samples.Length / elapsed.Elapsed.TotalSeconds;
                p50[round] = ToNanoseconds(Percentile(samples, 0.50));
                p95[round] = ToNanoseconds(Percentile(samples, 0.95));
                p99[round] = ToNanoseconds(Percentile(samples, 0.99));
                maxima[round] = ToNanoseconds(samples[^1]);
                Console.WriteLine(
                    $"[PreCreditConcurrentFastRound] producers={producers} round={round + 1} " +
                    $"throughputOpsPerSec={throughputs[round]:F0} " +
                    $"p50Ns={p50[round]:F1} p95Ns={p95[round]:F1} " +
                    $"p99Ns={p99[round]:F1} maxNs={maxima[round]:F1}");
            }

            Console.WriteLine(
                $"[PreCreditConcurrentFast] producers={producers} payloadBytes={PayloadBytes} " +
                $"rounds={MeasuredRounds} operationsPerRound={producers * MeasuredOperationsPerProducer} " +
                $"throughputOpsPerSec={Median(throughputs):F0} " +
                $"p50Ns={Median(p50):F1} p95Ns={Median(p95):F1} " +
                $"p99Ns={Median(p99):F1} maxNs={Median(maxima):F1} " +
                $"serializeCount={codec.SerializeCount} " +
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

    private static async Task RunPhaseAsync(
        RpcSession session,
        int producers,
        int operationsPerProducer,
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
                var item = new UnsizedPayload(PayloadBytes);
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

    private static SharpLinkRuntimeContext CreateContext(UnsizedPayloadCodec codec)
    {
        var builder = new SharpLinkRuntimeContextBuilder()
            .AddCodec(codec);
        builder.Configure(options =>
        {
            // Keep transport/send-pump capacity comfortably above this evidence workload so the
            // measured limiter is pre-credit admission rather than downstream queue capacity.
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
}
