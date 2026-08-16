using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Issue #163 Phase-0 evidence probe: attributes SendPump latency for mixed
/// bulk-stream / tiny-unary / protocol-progress traffic on one connection.
///
/// The probe drives a real <see cref="RpcSession"/> SendPump over a custom
/// <see cref="PipeWriter"/> that models transport bandwidth. Every probe frame
/// embeds a 16-byte tag (int32 class id + int64 sequence) at the payload start,
/// so the transport boundary can attribute per-frame timings without touching
/// the production hot path. Reported phases:
///   capacity wait  - producer time inside capacity admission (backpressure path)
///   queue residence - acceptance to transport copy start (FIFO position cost)
///   copy            - Span.CopyTo into the transport buffer (#133 boundary)
///   batch wait      - last frame copy to FlushAsync (batching cost, #125 boundary)
///   transport write - FlushAsync completion (modeled bandwidth)
///   end to end      - acceptance to flush completion
///
/// Probe frames are wire-format-shaped for pump purposes only: the payload tag
/// precedes the class payload and no protocol peer parses these frames.
/// Evidence only: this instrumentation never runs in production builds.
/// </summary>
internal static class ProbeFrameClass
{
    internal const int Bulk = 1;
    internal const int Unary = 2;
    internal const int Progress = 3;

    /// <summary>
    /// Cancellation traffic: production keeps Cancel in the normal class to
    /// preserve request-before-cancel ordering, so it receives neither the
    /// progress headroom nor the priority drain. It is reported separately so
    /// the evidence cannot misattribute it to the reserved progress path.
    /// </summary>
    internal const int Cancel = 4;

    internal const long BulkSampleMask = 63; // residence sampled every 64th bulk frame
}

public static class SendPumpIsolationEvidenceRunner
{
    private const int TagOffset = ProtocolV2Constants.HeaderBytes;
    private const int TagBytes = sizeof(int) + sizeof(long);

    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(2);

    internal static async Task RunAsync(string[] args)
    {
        var scenario = GetOption(args, "--scenario") ?? "all";
        var profileText = GetOption(args, "--profile") ?? "balanced";
        var profile = profileText.ToLowerInvariant() switch
        {
            "lowlatency" => SharpLinkPerformanceProfile.LowLatency,
            "throughput" => SharpLinkPerformanceProfile.Throughput,
            "balanced" => SharpLinkPerformanceProfile.Balanced,
            _ => throw new ArgumentOutOfRangeException(nameof(profileText))
        };
        var rateBytesPerSecond = GetNonNegativeOption(args, "--transport-rate-bytes-per-second", 0L);
        var payloadBytes = (int)GetPositiveOption(args, "--payload-bytes", 16 * 1024);
        var bulkProducers = (int)GetPositiveOption(args, "--bulk-producers", 4);
        var maxSendQueueBytes = (int?)GetPositiveOptionOrNull(args, "--max-send-queue-bytes");
        var unaryIntervalMilliseconds = GetPositiveOption(args, "--unary-interval-ms", 1d);
        var progressIntervalMilliseconds = GetPositiveOption(args, "--progress-interval-ms", 1d);
        var durationSeconds = GetPositiveOption(args, "--duration-seconds", 10d);
        var bulkMode = (GetOption(args, "--bulk-mode") ?? "wait").ToLowerInvariant();
        var stallFlushes = args.Any(static a => a == "--stall");
        var outputPath = GetOption(args, "--output") ?? Path.Combine(
            "artifacts", "performance", "current", "send-pump-isolation.json");

        if (bulkMode is not ("wait" or "failfast"))
            throw new ArgumentOutOfRangeException(nameof(bulkMode));

        var config = new ProbeConfig(
            profile,
            rateBytesPerSecond,
            payloadBytes,
            bulkProducers,
            maxSendQueueBytes,
            unaryIntervalMilliseconds,
            progressIntervalMilliseconds,
            durationSeconds,
            bulkMode,
            stallFlushes);

        var scenarios = ResolveScenarios(scenario);
        var results = new List<ScenarioResult>(scenarios.Count);
        Console.WriteLine(
            "SendPump isolation evidence: profile={0} rate={1} B/s payload={2} B bulkProducers={3} " +
            "queue={4} unaryInterval={5}ms progressInterval={6}ms duration={7}s bulkMode={8} stall={9}",
            profile, rateBytesPerSecond, payloadBytes, bulkProducers,
            maxSendQueueBytes?.ToString() ?? "profile-default", unaryIntervalMilliseconds,
            progressIntervalMilliseconds, durationSeconds, bulkMode, stallFlushes);
        foreach (var name in scenarios)
        {
            var result = await MeasureScenarioAsync(name, config).ConfigureAwait(false);
            results.Add(result);
            Console.WriteLine(result.SummaryLine());
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true
        })).ConfigureAwait(false);
        Console.WriteLine($"SendPump isolation evidence: {fullPath}");
    }

    private static readonly string[] SupportedScenarios =
        ["unary-baseline", "stream-baseline", "sat-unary", "sat-progress", "window-update", "cancel-burst", "goaway"];

    private static List<string> ResolveScenarios(string scenario)
    {
        if (scenario == "all")
            return [.. SupportedScenarios];
        if (!SupportedScenarios.Contains(scenario, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scenario), scenario, $"Unsupported scenario; supported: {string.Join(", ", SupportedScenarios)} or all.");
        }
        return [scenario];
    }

    private static async Task<ScenarioResult> MeasureScenarioAsync(string name, ProbeConfig config)
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options =>
            {
                options.PerformanceProfile = config.Profile;
                if (config.MaxSendQueueBytes is { } queueBytes)
                    options.FlowControl.MaxSendQueueBytes = queueBytes;
            })
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var recorder = new ProbeRecorder();
        var output = new IsolationProbePipeWriter(config.RateBytesPerSecond, config.StallFlushes, recorder);
        var session = new RpcSession(
            new ProbeTransportConnection($"issue163-{name}", input.Reader, output),
            new RpcSessionCreationOptions(RpcSessionRole.Client, context));
        if (!session.TryCompleteHandshake(new NegotiatedSessionOptions(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                context.Protocol.MaxFramePayloadBytes,
                context.FlowControl.StreamReceiveWindowBytes,
                context.FlowControl.ConnectionReceiveWindowBytes)))
        {
            throw new InvalidOperationException($"Probe session handshake failed for scenario {name}.");
        }

        var queue = config.MaxSendQueueBytes ?? context.FlowControl.MaxSendQueueBytes;
        var queueBytesSampler = new Sampler();
        using var stop = new CancellationTokenSource();
        var producers = new List<Task>();
        try
        {
            if (name is "unary-baseline")
            {
                producers.Add(RunQuietly(() => UnaryProducerAsync(
                    session, recorder, TimeSpan.FromMilliseconds(config.UnaryIntervalMilliseconds),
                    waitForCapacity: false, stop.Token)));
            }
            else if (name is "stream-baseline")
            {
                for (var i = 0; i < config.BulkProducers; i++)
                {
                    var producerIndex = i;
                    producers.Add(RunQuietly(() => BulkProducerAsync(
                        session, recorder, config, producerIndex, stop.Token)));
                }
            }
            else
            {
                for (var i = 0; i < config.BulkProducers; i++)
                {
                    var producerIndex = i;
                    producers.Add(RunQuietly(() => BulkProducerAsync(
                        session, recorder, config, producerIndex, stop.Token)));
                }
                switch (name)
                {
                    case "sat-unary":
                        producers.Add(RunQuietly(() => UnaryProducerAsync(
                            session, recorder, TimeSpan.FromMilliseconds(config.UnaryIntervalMilliseconds),
                            waitForCapacity: false, stop.Token)));
                        break;
                    case "sat-progress":
                        producers.Add(RunQuietly(() => ProgressProducerAsync(
                            session, recorder, ProgressKind.Ping,
                            TimeSpan.FromMilliseconds(config.ProgressIntervalMilliseconds), stop.Token)));
                        break;
                    case "window-update":
                        producers.Add(RunQuietly(() => ProgressProducerAsync(
                            session, recorder, ProgressKind.WindowUpdate,
                            TimeSpan.FromMilliseconds(config.ProgressIntervalMilliseconds), stop.Token)));
                        break;
                    case "cancel-burst":
                        producers.Add(RunQuietly(() => ProgressProducerAsync(
                            session, recorder, ProgressKind.CancelBurst,
                            TimeSpan.FromMilliseconds(config.ProgressIntervalMilliseconds), stop.Token)));
                        break;
                    case "goaway":
                        producers.Add(RunQuietly(() => GoAwayProducerAsync(
                            session, recorder, TimeSpan.FromMilliseconds(config.ProgressIntervalMilliseconds),
                            stop.Token)));
                        break;
                }
            }

            producers.Add(RunQuietly(() => QueueSamplerAsync(session, queueBytesSampler, stop.Token)));

            // Warmup, then measure over a clean window.
            // A stalled transport cannot drain, so the progress headroom
            // admission happens while the queue fills for the first time:
            // measure from the cold queue instead of discarding that fill in
            // a warmup.
            if (config.StallFlushes)
            {
                recorder.BeginMeasurement();
                output.BeginMeasurement();
                queueBytesSampler.Clear();
            }
            else
            {
                Console.WriteLine("[Probe] warmup complete, starting measurement window");
                await Task.Delay(Warmup, stop.Token).ConfigureAwait(false);
                recorder.BeginMeasurement();
                output.BeginMeasurement();
                queueBytesSampler.Clear();
            }
            var measurementStarted = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromSeconds(config.DurationSeconds), stop.Token).ConfigureAwait(false);
            var measurementStopped = Stopwatch.GetTimestamp();
            output.EndMeasurement();

            await stop.CancelAsync().ConfigureAwait(false);
            Console.WriteLine("[Probe] measurement window done, awaiting producers");
            await Task.WhenAll(producers).ConfigureAwait(false);
            Console.WriteLine("[Probe] producers stopped, awaiting drain");
            if (config.StallFlushes)
                output.ReleaseStalledFlushes();
            await output.WaitForDrainAsync().ConfigureAwait(false);
            Console.WriteLine("[Probe] drain complete");
            // Synchronize with the session pump: the transport watermarks do
            // not prove the pump finished issuing and recording flushes (with
            // --stall it can still be waking from the stall release).
            await session.FlushSendQueueAsync().ConfigureAwait(false);
            var elapsed = Math.Max(0.001, Stopwatch.GetElapsedTime(measurementStarted, measurementStopped).TotalSeconds);
            Console.WriteLine("[Probe] building result");
            var built = BuildResult(name, config, recorder, output, queueBytesSampler, queue, elapsed);
            Console.WriteLine("[Probe] result built");
            return built;
        }
        finally
        {
            stop.Cancel();
            output.ReleaseStalledFlushes();
            Console.WriteLine("[Probe] disposing session");
            await session.DisposeAsync().ConfigureAwait(false);
            Console.WriteLine("[Probe] session disposed");
            await input.Writer.CompleteAsync().ConfigureAwait(false);
            Console.WriteLine("[Probe] completing writer");
            await output.CompleteAsync().ConfigureAwait(false);
            Console.WriteLine("[Probe] writer completed");
            context.Dispose();
        }
    }

    private static Task RunQuietly(Func<Task> body) => Task.Run(async () =>
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProbeProducer] terminated: {ex.GetType().Name}: {ex.Message}");
        }
    });

    private static async Task QueueSamplerAsync(
        RpcSession session,
        Sampler sampler,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            sampler.Record(session.QueuedSendBytes);
        }
    }

    private static ScenarioResult BuildResult(
        string name,
        ProbeConfig config,
        ProbeRecorder recorder,
        IsolationProbePipeWriter output,
        Sampler queueBytesSampler,
        int queueBytes,
        double elapsedSeconds)
    {
        var bulk = recorder.BuildClassStats(ProbeFrameClass.Bulk);
        var unary = recorder.BuildClassStats(ProbeFrameClass.Unary);
        var progress = recorder.BuildClassStats(ProbeFrameClass.Progress);
        var cancel = recorder.BuildClassStats(ProbeFrameClass.Cancel);
        var transportRate = output.MeasuredBytes / elapsedSeconds;
        return new ScenarioResult(
            name,
            config,
            queueBytes,
            bulk,
            unary,
            progress,
            cancel,
            new ScenarioSummary(
                queueBytesSampler.Mean(),
                queueBytesSampler.Max(),
                output.MeasuredBytes,
                output.FlushCount,
                output.MaxBatchBytes,
                output.MeanBatchBytes,
                elapsedSeconds,
                transportRate));
    }

    // ----- producers -----------------------------------------------------

    private static async Task BulkProducerAsync(
        RpcSession session,
        ProbeRecorder recorder,
        ProbeConfig config,
        int producerIndex,
        CancellationToken cancellationToken)
    {
        var buffers = session.RuntimeContext.Buffers;
        var payload = BuildBulkPayload(config.PayloadBytes);
        var requestId = unchecked((ulong)(0x1630 + producerIndex));
        while (!cancellationToken.IsCancellationRequested)
        {
            var seq = recorder.NextSequence();
            var writer = buffers.Rent();
            try
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.StreamData,
                           ProtocolV2FrameFlags.None,
                           requestId))
                {
                    WriteTag(writer, ProbeFrameClass.Bulk, seq);
                    WriteBytes(writer, payload);
                }
            }
            catch
            {
                buffers.Return(writer);
                throw;
            }

            recorder.RecordAttempt(ProbeFrameClass.Bulk);
            if (config.BulkMode == "wait")
            {
                var waitStarted = Stopwatch.GetTimestamp();
                try
                {
                    await session.SendPacketWithBackpressureAsync(writer, cancellationToken)
                        .ConfigureAwait(false);
                    recorder.RecordCapacityWait(ProbeFrameClass.Bulk, waitStarted);
                    recorder.RecordAcceptance(ProbeFrameClass.Bulk, seq, Stopwatch.GetTimestamp());
                }
                catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                    recorder.RecordFull(ProbeFrameClass.Bulk);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    return;
                }
            }
            else
            {
                try
                {
                    session.SendPacket(writer);
                    recorder.RecordAcceptance(ProbeFrameClass.Bulk, seq, Stopwatch.GetTimestamp());
                }
                catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                    recorder.RecordFull(ProbeFrameClass.Bulk);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    private static async Task UnaryProducerAsync(
        RpcSession session,
        ProbeRecorder recorder,
        TimeSpan interval,
        bool waitForCapacity,
        CancellationToken cancellationToken)
    {
        var buffers = session.RuntimeContext.Buffers;
        var payload = new byte[64];
        var requestId = 0x1651UL;
        while (true)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            var seq = recorder.NextSequence();
            var writer = buffers.Rent();
            try
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.Response,
                           ProtocolV2FrameFlags.None,
                           requestId))
                {
                    WriteTag(writer, ProbeFrameClass.Unary, seq);
                    WriteBytes(writer, payload);
                }
            }
            catch
            {
                buffers.Return(writer);
                throw;
            }

            recorder.RecordAttempt(ProbeFrameClass.Unary);
            try
            {
                if (waitForCapacity)
                {
                    var waitStarted = Stopwatch.GetTimestamp();
                    await session.SendPacketWithBackpressureAsync(writer, cancellationToken)
                        .ConfigureAwait(false);
                    recorder.RecordCapacityWait(ProbeFrameClass.Unary, waitStarted);
                    recorder.RecordAcceptance(ProbeFrameClass.Unary, seq, Stopwatch.GetTimestamp());
                }
                else
                {
                    session.SendPacket(writer);
                    recorder.RecordAcceptance(ProbeFrameClass.Unary, seq, Stopwatch.GetTimestamp());
                }
            }
            catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
            {
                recorder.RecordFull(ProbeFrameClass.Unary);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    private enum ProgressKind
    {
        Ping,
        WindowUpdate,
        CancelBurst
    }

    private static async Task ProgressProducerAsync(
        RpcSession session,
        ProbeRecorder recorder,
        ProgressKind kind,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var buffers = session.RuntimeContext.Buffers;
        const ushort streamId = 7;
        var requestId = 0x1653UL;
        while (true)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            var burst = kind == ProgressKind.CancelBurst ? 10 : 1;
            for (var i = 0; i < burst; i++)
            {
                var seq = recorder.NextSequence();
                var writer = buffers.Rent();
                try
                {
                    using (writer.BeginPacketScope(
                               kind switch
                               {
                                   ProgressKind.Ping => ProtocolV2FrameType.Ping,
                                   ProgressKind.CancelBurst => ProtocolV2FrameType.Cancel,
                                   _ => ProtocolV2FrameType.WindowUpdate
                               },
                               ProtocolV2FrameFlags.None,
                               kind == ProgressKind.Ping ? 0UL : requestId))
                    {
                        WriteTag(writer, kind == ProgressKind.CancelBurst
                            ? ProbeFrameClass.Cancel
                            : ProbeFrameClass.Progress, seq);
                        switch (kind)
                        {
                            case ProgressKind.Ping:
                                {
                                    var span = writer.GetSpan(sizeof(long));
                                    BinaryPrimitives.WriteInt64LittleEndian(
                                        span, session.RuntimeContext.TimeProvider.GetTimestamp());
                                    writer.Advance(sizeof(long));
                                    break;
                                }
                            case ProgressKind.WindowUpdate:
                                ProtocolV2PayloadCodec.WriteWindowUpdate(
                                    writer,
                                    new ProtocolV2WindowUpdate(streamId, 64 * 1024));
                                break;
                            default:
                                break;
                        }
                    }
                }
                catch
                {
                    buffers.Return(writer);
                    throw;
                }

                var frameClass = kind == ProgressKind.CancelBurst
                    ? ProbeFrameClass.Cancel
                    : ProbeFrameClass.Progress;
                recorder.RecordAttempt(frameClass);
                try
                {
                    session.SendPacket(writer);
                    recorder.RecordAcceptance(frameClass, seq, Stopwatch.GetTimestamp());
                }
                catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                    recorder.RecordFull(frameClass);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    private static async Task GoAwayProducerAsync(
        RpcSession session,
        ProbeRecorder recorder,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var buffers = session.RuntimeContext.Buffers;
        while (true)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            var seq = recorder.NextSequence();
            var writer = buffers.Rent();
            try
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.GoAway,
                           ProtocolV2FrameFlags.Error,
                           0))
                {
                    WriteTag(writer, ProbeFrameClass.Progress, seq);
                    var span = writer.GetSpan(sizeof(ulong));
                    BinaryPrimitives.WriteUInt64LittleEndian(span, 0x1653UL);
                    writer.Advance(sizeof(ulong));
                    ProtocolV2PayloadCodec.WriteError(
                        writer,
                        SharpLinkErrorCode.Unavailable,
                        "probe-drain",
                        session.RuntimeContext.Protocol.MaxErrorMessageBytes,
                        out _);
                }
            }
            catch
            {
                buffers.Return(writer);
                throw;
            }

            recorder.RecordAttempt(ProbeFrameClass.Progress);
            try
            {
                var started = Stopwatch.GetTimestamp();
                // The production GoAway path is send-with-backpressure + force
                // flush. Record acceptance before the call: the writer takes
                // the acceptance during the flush, which completes before this
                // call returns, so a post-await timestamp would always be
                // dropped. The pre-call timestamp approximates the enqueue
                // boundary; the full wait+flush cost stays in CapacityWait.
                recorder.RecordAcceptance(ProbeFrameClass.Progress, seq, started);
                await session.SendPacketAndFlushAsync(writer, cancellationToken).ConfigureAwait(false);
                recorder.RecordCapacityWait(ProbeFrameClass.Progress, started);
            }
            catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
            {
                recorder.RecordFull(ProbeFrameClass.Progress);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    // ----- frame helpers -------------------------------------------------

    private static byte[] BuildBulkPayload(int payloadBytes)
    {
        var payload = new byte[payloadBytes];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = unchecked((byte)((i * 31) + 17));
        return payload;
    }

    private static void WriteBytes(IRpcByteBufferWriter writer, byte[] payload)
    {
        var span = writer.GetSpan(payload.Length);
        payload.CopyTo(span);
        writer.Advance(payload.Length);
    }

    private static void WriteTag(IRpcByteBufferWriter writer, int classId, long seq)
    {
        var span = writer.GetSpan(TagBytes);
        BinaryPrimitives.WriteInt32LittleEndian(span, classId);
        BinaryPrimitives.WriteInt64LittleEndian(span[sizeof(int)..], seq);
        writer.Advance(TagBytes);
    }

    // ----- option parsing ------------------------------------------------

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        }
        return null;
    }

    private static long GetNonNegativeOption(string[] args, string name, long defaultValue)
    {
        var value = GetOption(args, name);
        if (value is null)
            return defaultValue;
        var parsed = long.Parse(value, CultureInfo.InvariantCulture);
        return parsed >= 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }

    private static long GetPositiveOption(string[] args, string name, long defaultValue)
    {
        var value = GetOption(args, name);
        if (value is null)
            return defaultValue;
        var parsed = long.Parse(value, CultureInfo.InvariantCulture);
        return parsed > 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }

    private static double GetPositiveOption(string[] args, string name, double defaultValue)
    {
        var value = GetOption(args, name);
        if (value is null)
            return defaultValue;
        var parsed = double.Parse(value, CultureInfo.InvariantCulture);
        return parsed > 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }

    private static long? GetPositiveOptionOrNull(string[] args, string name)
    {
        var value = GetOption(args, name);
        if (value is null)
            return null;
        var parsed = long.Parse(value, CultureInfo.InvariantCulture);
        return parsed > 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }
}

// ----- result model -------------------------------------------------------

public sealed record ProbeConfig(
    SharpLinkPerformanceProfile Profile,
    long RateBytesPerSecond,
    int PayloadBytes,
    int BulkProducers,
    int? MaxSendQueueBytes,
    double UnaryIntervalMilliseconds,
    double ProgressIntervalMilliseconds,
    double DurationSeconds,
    string BulkMode,
    bool StallFlushes);

public sealed record ScenarioResult(
    string Scenario,
    ProbeConfig Config,
    int QueueBytes,
    ClassStats Bulk,
    ClassStats Unary,
    ClassStats Progress,
    ClassStats Cancel,
    ScenarioSummary Summary)
{
    internal string SummaryLine() => string.Format(
        CultureInfo.InvariantCulture,
        "[Result] {0}: bulk={1:F2} MiB/s fullBulk={2:F4} | unary full={3:F4} resP50={4} resP99={5} " +
        "capP99={6} | progress full={7:F4} resP50={8} resP99={9} | batchP99={10} txP99={11} " +
        "queueMean={12} queueMax={13} cancelFull={14:F4} cancelResP99={15}",
        Scenario,
        Summary.TransportMiBPerSecond,
        Bulk.FullRate,
        Unary.FullRate,
        Unary.ResidenceP50,
        Unary.ResidenceP99,
        Unary.CapacityWaitP99,
        Progress.FullRate,
        Progress.ResidenceP50,
        Progress.ResidenceP99,
        Unary.BatchWaitP99,
        Unary.TransportWriteP99,
        Summary.QueueBytesMean,
        Summary.QueueBytesMax,
        Cancel.FullRate,
        Cancel.ResidenceP99);
}

public sealed record ClassStats(
    long Attempts,
    long Accepted,
    long Full,
    double FullRate,
    string ResidenceP50,
    string ResidenceP99,
    string ResidenceP999,
    string ResidenceMax,
    double ResidenceMeanMicroseconds,
    string CapacityWaitP50,
    string CapacityWaitP99,
    string CapacityWaitP999,
    double CapacityWaitMeanMicroseconds,
    double CopyMeanMicroseconds,
    string BatchWaitP99,
    double BatchWaitMeanMicroseconds,
    string TransportWriteP99,
    double TransportWriteMeanMicroseconds,
    string EndToEndP99,
    double EndToEndMeanMicroseconds);

public sealed record ScenarioSummary(
    double QueueBytesMean,
    long QueueBytesMax,
    long TransportBytes,
    long FlushCount,
    long MaxBatchBytes,
    double MeanBatchBytes,
    double MeasurementSeconds,
    double TransportBytesPerSecond)
{
    internal double TransportMiBPerSecond => TransportBytesPerSecond / (1024d * 1024d);
}

// ----- recorder -----------------------------------------------------------

internal sealed class ProbeRecorder
{

    private readonly object _gate = new();
    private readonly Dictionary<long, long> _acceptances = new();
    private readonly List<FrameSample> _samples = new();
    private readonly Dictionary<int, ClassAccumulator> _classes = new()
    {
        [1] = new(),
        [2] = new(),
        [3] = new(),
        [4] = new()
    };
    private long _sequence;
    private bool _measuring;

    internal long NextSequence() => Interlocked.Increment(ref _sequence);

    internal void BeginMeasurement()
    {
        lock (_gate)
        {
            _samples.Clear();
            foreach (var accumulator in _classes.Values)
                accumulator.Clear();
            _acceptances.Clear();
            _measuring = true;
        }
    }

    internal void RecordAttempt(int classId)
    {
        if (_measuring)
            _classes[classId].Attempts.Increment();
    }

    internal void RecordFull(int classId)
    {
        if (_measuring)
            _classes[classId].Full.Increment();
    }

    internal void RecordCapacityWait(int classId, long started)
    {
        if (!_measuring)
            return;
        var microseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        lock (_gate)
            _classes[classId].CapacityWait.Add(microseconds);
    }

    internal void RecordAcceptance(int classId, long seq, long acceptedAt)
    {
        if (!_measuring)
            return;
        if (classId == ProbeFrameClass.Bulk && (seq & ProbeFrameClass.BulkSampleMask) != 0)
            return;
        lock (_gate)
            _acceptances[seq] = acceptedAt;
    }


    internal bool TryTakeAcceptance(long seq, out long acceptedAt)
    {
        lock (_gate)
        {
            if (_measuring && _acceptances.Remove(seq, out acceptedAt))
                return true;
        }
        acceptedAt = 0;
        return false;
    }

    internal void RecordSample(FrameSample sample)
    {
        lock (_gate)
        {
            if (_measuring)
                _samples.Add(sample);
        }
    }

    internal ClassStats BuildClassStats(int classId)
    {
        lock (_gate)
        {
            var accumulator = _classes[classId];
            var residence = new List<double>();
            var copy = new List<double>();
            var batchWait = new List<double>();
            var transport = new List<double>();
            var endToEnd = new List<double>();
            foreach (var sample in _samples)
            {
                if (sample.ClassId != classId)
                    continue;
                residence.Add(Stopwatch.GetElapsedTime(sample.AcceptedAt, sample.CopyStart).TotalMicroseconds);
                copy.Add(Stopwatch.GetElapsedTime(sample.CopyStart, sample.CopyEnd).TotalMicroseconds);
                batchWait.Add(Stopwatch.GetElapsedTime(sample.CopyEnd, sample.FlushStart).TotalMicroseconds);
                transport.Add(Stopwatch.GetElapsedTime(sample.FlushStart, sample.FlushEnd).TotalMicroseconds);
                endToEnd.Add(Stopwatch.GetElapsedTime(sample.AcceptedAt, sample.FlushEnd).TotalMicroseconds);
            }

            var attempts = accumulator.Attempts.Read();
            var full = accumulator.Full.Read();
            var accepted = attempts - full;
            var fullRate = attempts == 0 ? 0 : full / (double)attempts;
            return new ClassStats(
                attempts,
                accepted,
                full,
                fullRate,
                FormatMicroseconds(Percentile(residence, 0.5)),
                FormatMicroseconds(Percentile(residence, 0.99)),
                FormatMicroseconds(Percentile(residence, 0.999)),
                FormatMicroseconds(residence.Count == 0 ? 0 : residence.Max()),
                residence.Count == 0 ? 0 : residence.Average(),
                FormatMicroseconds(Percentile(accumulator.CapacityWait, 0.5)),
                FormatMicroseconds(Percentile(accumulator.CapacityWait, 0.99)),
                FormatMicroseconds(Percentile(accumulator.CapacityWait, 0.999)),
                accumulator.CapacityWait.Count == 0 ? 0 : accumulator.CapacityWait.Average(),
                copy.Count == 0 ? 0 : copy.Average(),
                FormatMicroseconds(Percentile(batchWait, 0.99)),
                batchWait.Count == 0 ? 0 : batchWait.Average(),
                FormatMicroseconds(Percentile(transport, 0.99)),
                transport.Count == 0 ? 0 : transport.Average(),
                FormatMicroseconds(Percentile(endToEnd, 0.99)),
                endToEnd.Count == 0 ? 0 : endToEnd.Average());
        }
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        values.Sort();
        var index = Math.Min(values.Count - 1, (int)(percentile * values.Count));
        return values[index];
    }

    private static string FormatMicroseconds(double value)
        => value.ToString("F1", CultureInfo.InvariantCulture) + "us";

    private sealed class ClassAccumulator
    {
        internal readonly InterlockedCounter Attempts = new();
        internal readonly InterlockedCounter Full = new();
        internal readonly List<double> CapacityWait = [];

        internal void Clear()
        {
            Attempts.Reset();
            Full.Reset();
            CapacityWait.Clear();
        }
    }

    private sealed class InterlockedCounter
    {
        private long _value;

        internal void Increment() => Interlocked.Increment(ref _value);
        internal long Read() => Interlocked.Read(ref _value);
        internal void Reset() => Interlocked.Exchange(ref _value, 0);
    }
}

internal readonly record struct FrameSample(
    int ClassId,
    long Seq,
    long AcceptedAt,
    long CopyStart,
    long CopyEnd,
    long FlushStart,
    long FlushEnd);

internal sealed class Sampler
{
    private readonly object _gate = new();
    private readonly List<long> _values = [];

    internal void Record(long value)
    {
        lock (_gate)
            _values.Add(value);
    }

    internal void Clear()
    {
        lock (_gate)
            _values.Clear();
    }

    internal double Mean()
    {
        lock (_gate)
            return _values.Count == 0 ? 0 : _values.Average();
    }

    internal long Max()
    {
        lock (_gate)
            return _values.Count == 0 ? 0 : _values.Max();
    }
}

// ----- transport model ----------------------------------------------------

/// <summary>
/// Models a bandwidth-limited transport at the SendPump boundary. Each flush
/// completes only after the flushed bytes have been consumed by a paced drain
/// task, so queue admission, FIFO residence, batching, and transport write all
/// behave as they do against a real slow socket. An unbounded rate models a
/// fast loopback; <c>--stall</c> keeps flushes pending until released.
///
/// The drain barrier uses monotonic flushed/consumed watermarks instead of a
/// per-flush byte counter: a drain read may finish before the flush records its
/// byte count, so a signed counter can lose the wake-up (observed as a
/// permanently pending batch). Cumulative totals can only make the consumed
/// watermark catch up, never overshoot below the flushed watermark.
/// </summary>
internal sealed class IsolationProbePipeWriter : PipeWriter
{
    private const int TagOffset = ProtocolV2Constants.HeaderBytes;
    private const int TagBytes = sizeof(int) + sizeof(long);

    private readonly Pipe _pipe = new(new PipeOptions(
        pauseWriterThreshold: 0,
        resumeWriterThreshold: 0,
        useSynchronizationContext: false));
    private readonly long _rateBytesPerSecond;
    private readonly bool _stallFlushes;
    private readonly ProbeRecorder _recorder;
    private readonly CancellationTokenSource _drainCts = new();
    private readonly Task _drainTask;
    private readonly TaskCompletionSource<bool> _stallRelease = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private List<FrameSample> _pendingBatch = [];
    private long _unflushedBytes;
    private long _flushedTotal;
    private long _consumedTotal;
    private TaskCompletionSource<bool>? _flushBarrier;
    private long _measuredBytes;
    private long _flushCount;
    private long _maxBatchBytes;
    private long _totalBatchBytes;
    private long _batchCount;
    private bool _measuringWindow;
    private bool _measuringSamples;
    private long _copyStart;
    private Memory<byte> _lastMemory;

    internal IsolationProbePipeWriter(long rateBytesPerSecond, bool stallFlushes, ProbeRecorder recorder)
    {
        _rateBytesPerSecond = rateBytesPerSecond;
        _stallFlushes = stallFlushes;
        _recorder = recorder;
        _drainTask = Task.Run(DrainAsync);
    }

    internal long MeasuredBytes => Interlocked.Read(ref _measuredBytes);
    internal long FlushCount => Interlocked.Read(ref _flushCount);
    internal long MaxBatchBytes => Interlocked.Read(ref _maxBatchBytes);
    internal double MeanBatchBytes => _batchCount == 0
        ? 0
        : Interlocked.Read(ref _totalBatchBytes) / (double)Interlocked.Read(ref _batchCount);

    internal void BeginMeasurement()
    {
        lock (_gate)
        {
            _measuredBytes = 0;
            _flushCount = 0;
            _maxBatchBytes = 0;
            _totalBatchBytes = 0;
            _batchCount = 0;
            _measuringWindow = true;
            _measuringSamples = true;
        }
    }

    /// <summary>
    /// Stops the window byte/batch accounting at the end boundary. Latency
    /// sample recording continues through the drain so frames accepted during
    /// the window are still attributed after the window closes.
    /// </summary>
    internal void EndMeasurement()
    {
        Volatile.Write(ref _measuringWindow, false);
    }

    internal void ReleaseStalledFlushes()
        => _stallRelease.TrySetResult(true);

    internal Task WaitForDrainAsync()
        => WaitUntilConsumedAsync(CancellationToken.None);

    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        _copyStart = Stopwatch.GetTimestamp();
        _lastMemory = _pipe.Writer.GetMemory(sizeHint);
        return _lastMemory.Span;
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        _copyStart = Stopwatch.GetTimestamp();
        _lastMemory = _pipe.Writer.GetMemory(sizeHint);
        return _lastMemory;
    }

    public override void Advance(int bytes)
    {
        if (bytes == 0)
            return;

        // Extract the probe tag before Advance: once advanced, the segment may
        // be recycled by the drain reader.
        var classId = 0;
        long seq = 0;
        if (bytes >= TagOffset + TagBytes && !_lastMemory.IsEmpty)
        {
            var tag = _lastMemory.Span.Slice(TagOffset, TagBytes);
            classId = BinaryPrimitives.ReadInt32LittleEndian(tag);
            seq = BinaryPrimitives.ReadInt64LittleEndian(tag[sizeof(int)..]);
        }

        var copyEnd = Stopwatch.GetTimestamp();
        _lastMemory = default;
        _pipe.Writer.Advance(bytes);
        Interlocked.Add(ref _unflushedBytes, bytes);

        if (Volatile.Read(ref _measuringSamples) &&
            seq > 0 && (classId != ProbeFrameClass.Bulk || (seq & ProbeFrameClass.BulkSampleMask) == 0))
        {
            lock (_gate)
                _pendingBatch.Add(new FrameSample(classId, seq, 0, _copyStart, copyEnd, 0, 0));
        }
    }

    public override void CancelPendingFlush() => _pipe.Writer.CancelPendingFlush();

    public override void Complete(Exception? exception = null)
        => _pipe.Writer.Complete(exception);

    public override async ValueTask CompleteAsync(Exception? exception = null)
    {
        await _pipe.Writer.CompleteAsync(exception).ConfigureAwait(false);
        await _drainCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _drainTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        var flushStart = Stopwatch.GetTimestamp();
        List<FrameSample> batch;
        long batchBytes;
        lock (_gate)
        {
            batch = _pendingBatch;
            _pendingBatch = [];
            batchBytes = Interlocked.Exchange(ref _unflushedBytes, 0);
        }

        var result = await _pipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsCanceled || result.IsCompleted)
        {
            ReleasePendingBatch(batch);
            return result;
        }

        if (_stallFlushes)
            await _stallRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Record the flushed watermark AFTER the inner flush completes so the
        // drain can race ahead freely; the monotonic consumed watermark can
        // never fall behind a lost update. Transport bytes are counted at the
        // completed-flush boundary, not at copy time, so a stalled batch that
        // finishes after the measurement window contributes nothing.
        Interlocked.Add(ref _flushedTotal, batchBytes);
        await WaitUntilConsumedAsync(cancellationToken).ConfigureAwait(false);
        var flushEnd = Stopwatch.GetTimestamp();
        if (Volatile.Read(ref _measuringWindow))
            Interlocked.Add(ref _measuredBytes, batchBytes);

        if (Volatile.Read(ref _measuringWindow))
        {
            Interlocked.Increment(ref _flushCount);
            Interlocked.Add(ref _totalBatchBytes, batchBytes);
            Interlocked.Increment(ref _batchCount);
            var max = Interlocked.Read(ref _maxBatchBytes);
            while (batchBytes > max &&
                   Interlocked.CompareExchange(ref _maxBatchBytes, batchBytes, max) != max)
                max = Interlocked.Read(ref _maxBatchBytes);
        }

        foreach (var sample in batch)
        {
            if (!_recorder.TryTakeAcceptance(sample.Seq, out var acceptedAt))
                continue;
            _recorder.RecordSample(sample with
            {
                AcceptedAt = acceptedAt,
                FlushStart = flushStart,
                FlushEnd = flushEnd
            });
        }
        return result;
    }

    private void ReleasePendingBatch(List<FrameSample> batch)
    {
        foreach (var sample in batch)
            _recorder.TryTakeAcceptance(sample.Seq, out _);
    }

    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                var result = await _pipe.Reader.ReadAsync(_drainCts.Token).ConfigureAwait(false);
                var buffer = result.Buffer;
                var consumed = buffer.Length;
                if (consumed > 0)
                    await PaceAsync(consumed, _drainCts.Token).ConfigureAwait(false);
                _pipe.Reader.AdvanceTo(buffer.End);
                Interlocked.Add(ref _consumedTotal, consumed);
                SignalConsumed();
                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) when (_drainCts.IsCancellationRequested)
        {
        }
    }

    private async Task PaceAsync(long bytes, CancellationToken cancellationToken)
    {
        if (_rateBytesPerSecond <= 0)
            return;
        var seconds = bytes / (double)_rateBytesPerSecond;
        if (seconds <= 0)
            return;
        var target = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        // Sleep for all but the final ~2ms, then spin to the exact target.
        // Stopwatch ticks must be converted to TimeSpan ticks (100 ns) before
        // Task.Delay: on Linux Stopwatch.Frequency is 1 GHz, so raw tick
        // counts would oversleep ~100x.
        var spinThreshold = Stopwatch.Frequency / 500;
        var remaining = target - Stopwatch.GetTimestamp();
        if (remaining > spinThreshold)
        {
            var sleepTicks = (long)((remaining - spinThreshold) *
                (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency));
            await Task.Delay(TimeSpan.FromTicks(sleepTicks), cancellationToken).ConfigureAwait(false);
        }
        var spin = new SpinWait();
        while (Stopwatch.GetTimestamp() < target)
            spin.SpinOnce();
    }

    private void SignalConsumed()
    {
        TaskCompletionSource<bool>? barrier;
        lock (_gate)
        {
            if (Volatile.Read(ref _consumedTotal) < Volatile.Read(ref _flushedTotal))
                return;
            barrier = _flushBarrier;
            _flushBarrier = null;
        }
        barrier?.TrySetResult(true);
    }

    private async Task WaitUntilConsumedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task barrier;
            lock (_gate)
            {
                if (Volatile.Read(ref _consumedTotal) >= Volatile.Read(ref _flushedTotal))
                    return;
                _flushBarrier ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.None);
                barrier = _flushBarrier.Task;
            }
            await barrier.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class ProbeTransportConnection(
    string id,
    PipeReader input,
    PipeWriter output) : ITransportConnection
{
    public string Id { get; } = id;
    public PipeReader Input { get; } = input;
    public PipeWriter Output { get; } = output;
    public System.Net.EndPoint? LocalEndPoint => null;
    public System.Net.EndPoint? RemoteEndPoint => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
