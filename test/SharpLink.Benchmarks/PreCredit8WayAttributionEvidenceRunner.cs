using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class PreCredit8WayAttributionEvidenceRunner
{
    private const int Producers = 8;
    private const int PayloadBytes = 1024;
    private const int WarmupOperationsPerProducer = 1_000;
    private const int MeasuredOperationsPerProducer = 5_000;
    private const int WindowBytes = 16 * 1024 * 1024;

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 1)
        {
            throw new ArgumentException(
                "8-way attribution requires exactly one case: " +
                "full-current, full-devshape, flow-try, or flow-async.");
        }

        var caseName = args[0];
        ThreadPool.GetMinThreads(out var originalWorkerThreads, out var originalCompletionPortThreads);
        var raisedMinimum = originalWorkerThreads < Producers &&
            ThreadPool.SetMinThreads(Producers, originalCompletionPortThreads);
        try
        {
            var metrics = caseName switch
            {
                "full-current" => await RunFullCaseAsync(useCurrentPath: true).ConfigureAwait(false),
                "full-devshape" => await RunFullCaseAsync(useCurrentPath: false).ConfigureAwait(false),
                "flow-try" => await RunFlowCaseAsync(useTryAcquire: true).ConfigureAwait(false),
                "flow-async" => await RunFlowCaseAsync(useTryAcquire: false).ConfigureAwait(false),
                _ => throw new ArgumentException(
                    "Unknown 8-way attribution case. Expected full-current, full-devshape, " +
                    "flow-try, or flow-async.")
            };

            Console.WriteLine(
                $"[PreCredit8WayAttribution] case={caseName} producers={Producers} " +
                $"payloadBytes={PayloadBytes} operations={Producers * MeasuredOperationsPerProducer} " +
                $"throughputOpsPerSec={metrics.ThroughputOpsPerSec:F0} " +
                $"p50Ns={metrics.P50Ns:F1} p95Ns={metrics.P95Ns:F1} " +
                $"p99Ns={metrics.P99Ns:F1} maxNs={metrics.MaxNs:F1}");
        }
        finally
        {
            if (raisedMinimum)
                ThreadPool.SetMinThreads(originalWorkerThreads, originalCompletionPortThreads);
        }
    }

    private static async Task<RoundMetrics> RunFullCaseAsync(bool useCurrentPath)
    {
        var codec = new UnsizedPayloadCodec();
        using var context = CreateContext(codec);
        using var transport = new BenchmarkTransport(
            useCurrentPath ? "pre-credit-8way-full-current" : "pre-credit-8way-full-devshape");
        await using var session = CreateReadySession(transport, context);
        var item = new UnsizedPayload(PayloadBytes);

        await RunFullPhaseAsync(
            session,
            item,
            useCurrentPath,
            WarmupOperationsPerProducer,
            samples: null).ConfigureAwait(false);
        await session.FlushSendQueueAsync(CancellationToken.None).ConfigureAwait(false);
        await RunFullPhaseAsync(
            session,
            item,
            useCurrentPath,
            WarmupOperationsPerProducer,
            samples: null).ConfigureAwait(false);
        await session.FlushSendQueueAsync(CancellationToken.None).ConfigureAwait(false);

        var samples = new long[Producers * MeasuredOperationsPerProducer];
        var elapsed = Stopwatch.StartNew();
        await RunFullPhaseAsync(
            session,
            item,
            useCurrentPath,
            MeasuredOperationsPerProducer,
            samples).ConfigureAwait(false);
        elapsed.Stop();
        await session.FlushSendQueueAsync(CancellationToken.None).ConfigureAwait(false);

        if (session.PreCreditSerializedBytes != 0 || session.PreCreditSerializedWaiterCount != 0)
        {
            throw new InvalidOperationException(
                "Fast-consumer attribution unexpectedly retained pre-credit ownership.");
        }

        return Summarize(samples, elapsed.Elapsed);
    }

    private static async Task RunFullPhaseAsync(
        RpcSession session,
        UnsizedPayload item,
        bool useCurrentPath,
        int operationsPerProducer,
        long[]? samples)
    {
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Task[Producers];
        for (var producer = 0; producer < Producers; producer++)
        {
            var requestId = producer + 1L;
            var sampleOffset = producer * operationsPerProducer;
            workers[producer] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                if (useCurrentPath)
                {
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
                        session.ApplyWindowUpdate(
                            requestId,
                            new ProtocolV2WindowUpdate(1, PayloadBytes));
                    }
                    return;
                }

                for (var operation = 0; operation < operationsPerProducer; operation++)
                {
                    var started = Stopwatch.GetTimestamp();
                    await SendUnsizedDevShapeAsync(session, requestId, item).ConfigureAwait(false);
                    if (samples is not null)
                        samples[sampleOffset + operation] = Stopwatch.GetTimestamp() - started;
                    session.ApplyWindowUpdate(
                        requestId,
                        new ProtocolV2WindowUpdate(1, PayloadBytes));
                }
            });
        }

        start.TrySetResult(true);
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private static async ValueTask SendUnsizedDevShapeAsync(
        RpcSession session,
        long requestId,
        UnsizedPayload item)
    {
        // Benchmark-only reconstruction of dev's unsized fast-consumer shape, compiled against
        // the same PR runtime. It deliberately skips the PR pre-credit probe/budget and uses the
        // existing AcquireSendCreditAsync path after serialization.
        var codec = session.RuntimeContext.Codecs.GetCodec<UnsizedPayload>();
        var writer = session.RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.StreamData,
                       ProtocolV2FrameFlags.None,
                       unchecked((ulong)requestId)))
            {
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, 1);
                writer.Advance(sizeof(ushort));
                codec.Serialize(item, writer);
            }

            var encodedBytes = Math.Max(
                1,
                writer.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort));
            await session.AcquireStreamSendCreditAsync(
                requestId,
                1,
                encodedBytes,
                CancellationToken.None).ConfigureAwait(false);
            try
            {
                ownsWriter = false;
                session.SendPacket(writer);
            }
            catch
            {
                session.ReturnUnsentStreamCredit(requestId, 1, encodedBytes);
                throw;
            }
        }
        finally
        {
            if (ownsWriter)
                session.RuntimeContext.Buffers.Return(writer);
        }
    }

    private static async Task<RoundMetrics> RunFlowCaseAsync(bool useTryAcquire)
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var controller = new StreamFlowController(
            WindowBytes,
            WindowBytes,
            context.Protocol.MaxFramePayloadBytes,
            context.Protocol.MaxConcurrentStreamsPerConnection);

        await RunFlowPhaseAsync(
            controller,
            useTryAcquire,
            WarmupOperationsPerProducer,
            samples: null).ConfigureAwait(false);
        await RunFlowPhaseAsync(
            controller,
            useTryAcquire,
            WarmupOperationsPerProducer,
            samples: null).ConfigureAwait(false);

        var samples = new long[Producers * MeasuredOperationsPerProducer];
        var elapsed = Stopwatch.StartNew();
        await RunFlowPhaseAsync(
            controller,
            useTryAcquire,
            MeasuredOperationsPerProducer,
            samples).ConfigureAwait(false);
        elapsed.Stop();
        return Summarize(samples, elapsed.Elapsed);
    }

    private static async Task RunFlowPhaseAsync(
        StreamFlowController controller,
        bool useTryAcquire,
        int operationsPerProducer,
        long[]? samples)
    {
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Task[Producers];
        for (var producer = 0; producer < Producers; producer++)
        {
            var requestId = producer + 1L;
            var sampleOffset = producer * operationsPerProducer;
            workers[producer] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                if (useTryAcquire)
                {
                    for (var operation = 0; operation < operationsPerProducer; operation++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        if (!controller.TryAcquireSendCredit(requestId, 1, PayloadBytes))
                        {
                            throw new InvalidOperationException(
                                "Fast-consumer flow attribution unexpectedly missed TryAcquireSendCredit.");
                        }
                        if (samples is not null)
                            samples[sampleOffset + operation] = Stopwatch.GetTimestamp() - started;
                        controller.ApplyWindowUpdate(requestId, 1, PayloadBytes);
                    }
                    return;
                }

                for (var operation = 0; operation < operationsPerProducer; operation++)
                {
                    var started = Stopwatch.GetTimestamp();
                    await controller.AcquireSendCreditAsync(
                        requestId,
                        1,
                        PayloadBytes,
                        CancellationToken.None).ConfigureAwait(false);
                    if (samples is not null)
                        samples[sampleOffset + operation] = Stopwatch.GetTimestamp() - started;
                    controller.ApplyWindowUpdate(requestId, 1, PayloadBytes);
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
            options.FlowControl.MaxSendQueueBytes = 256 * 1024 * 1024;
            options.FlowControl.StreamReceiveWindowBytes = WindowBytes;
            options.FlowControl.ConnectionReceiveWindowBytes = WindowBytes;
        });
        return builder.Build(includeGeneratedAssemblyCatalog: false);
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
            WindowBytes,
            WindowBytes,
            null);
        if (!session.TryCompleteHandshake(negotiated))
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("8-way attribution handshake failed.");
        }
        return session;
    }

    private static RoundMetrics Summarize(long[] samples, TimeSpan elapsed)
    {
        Array.Sort(samples);
        return new RoundMetrics(
            samples.Length / elapsed.TotalSeconds,
            ToNanoseconds(Percentile(samples, 0.50)),
            ToNanoseconds(Percentile(samples, 0.95)),
            ToNanoseconds(Percentile(samples, 0.99)),
            ToNanoseconds(samples[^1]));
    }

    private static long Percentile(long[] sortedSamples, double percentile)
    {
        var index = (int)Math.Ceiling(sortedSamples.Length * percentile) - 1;
        return sortedSamples[Math.Clamp(index, 0, sortedSamples.Length - 1)];
    }

    private static double ToNanoseconds(long stopwatchTicks)
        => stopwatchTicks * (1_000_000_000d / Stopwatch.Frequency);

    private readonly record struct RoundMetrics(
        double ThroughputOpsPerSec,
        double P50Ns,
        double P95Ns,
        double P99Ns,
        double MaxNs);
}
