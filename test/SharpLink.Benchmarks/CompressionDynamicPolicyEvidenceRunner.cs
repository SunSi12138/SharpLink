using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;

namespace SharpLink.Benchmarks;

/// <summary>Issue #550 hot-path A/B evidence for dynamic compression policy publication.</summary>
public static class CompressionDynamicPolicyEvidenceRunner
{
    private const int PayloadBytes = 1024;
    private const int WarmupOperations = 512;
    private const int OperationsPerRound = 2048;
    private const int Rounds = 5;

    private static readonly SharpLinkCompressionSendPolicy EnabledPolicy = new()
    {
        Enabled = true,
        MinimumPayloadBytes = 0,
        MinimumSavingsBytes = 0,
        MinimumSavingsRatio = 0
    };

    private static readonly SharpLinkCompressionSendPolicy DisabledPolicy = new()
    {
        Enabled = false,
        MinimumPayloadBytes = 0,
        MinimumSavingsBytes = 0,
        MinimumSavingsRatio = 0
    };

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: --dynamic-compression-policy-evidence <output-json>");

        var outputPath = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var payload = new byte[PayloadBytes];
        Array.Fill(payload, (byte)0x2a);

        CompressionDynamicPolicyMeasurement noProvider;
        await using (var environment = await BenchmarkEnvironment.CreateAsync(
                         configureBuiltServer: static server =>
                             server.UpdateResponseCompressionPolicy(DisabledPolicy)).ConfigureAwait(false))
        {
            noProvider = await MeasureAsync(
                "no-provider",
                environment.Rpc,
                payload,
                clientProvider: null).ConfigureAwait(false);
        }

        var clientProvider = new HotPathProbeCompressionProvider();
        var serverProvider = new HotPathProbeCompressionProvider();
        await using var negotiated = await BenchmarkEnvironment.CreateAsync(
            configureServerRuntime: options => options.Compression.Providers.Add(serverProvider),
            configureClientRuntime: options => options.Compression.Providers.Add(clientProvider),
            configureBuiltServer: static server => server.UpdateResponseCompressionPolicy(DisabledPolicy),
            configureBuiltClient: static client => client.UpdateRequestCompressionPolicy(DisabledPolicy))
            .ConfigureAwait(false);

        var negotiatedDisabled = await MeasureAsync(
            "negotiated-disabled",
            negotiated.Rpc,
            payload,
            clientProvider).ConfigureAwait(false);
        Ensure(negotiatedDisabled.CompressionAttemptsPerOperation == 0,
            "a negotiated provider must not be invoked while the local request policy is disabled");

        negotiated.Client.UpdateRequestCompressionPolicy(EnabledPolicy);
        var enabled = await MeasureAsync(
            "enabled",
            negotiated.Rpc,
            payload,
            clientProvider).ConfigureAwait(false);
        Ensure(enabled.CompressionAttemptsPerOperation >= 1,
            "the enabled request policy must reach the negotiated provider on every measured request");

        negotiated.Client.UpdateRequestCompressionPolicy(DisabledPolicy);
        var afterUpdate = await MeasureAsync(
            "after-update-disabled",
            negotiated.Rpc,
            payload,
            clientProvider).ConfigureAwait(false);
        Ensure(afterUpdate.CompressionAttemptsPerOperation == 0,
            "the post-update hot path must observe the newly published disabled snapshot without reconnecting");

        var document = new CompressionDynamicPolicyEvidenceDocument
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Framework = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            PayloadBytes = PayloadBytes,
            OperationsPerRound = OperationsPerRound,
            Rounds = Rounds,
            Measurements = [noProvider, negotiatedDisabled, enabled, afterUpdate],
            Notes =
            [
                "All rows use the same unary byte[] RPC shape over loopback TCP; server Response compression is disabled so the measured policy branch is Client Request compression only.",
                "The negotiated rows reuse the same live connection. The transition negotiated-disabled -> enabled -> after-update-disabled is performed through ISharpLinkClient.UpdateRequestCompressionPolicy without reconnecting.",
                "The probe provider advertises a real negotiated wire profile but deliberately rejects every compression candidate after counting the attempt. This isolates policy-gate overhead from codec CPU while still executing the real RpcSession outbound compression gate.",
                "Allocated bytes and process CPU include the full client/server round trip and benchmark harness. Compare rows rather than interpreting them as the allocation cost of the snapshot read alone.",
                "Each reported value is the median of five independently measured rounds after warmup and full GC. Provider-attempt counters are semantic guards: no-provider, negotiated-disabled and after-update must stay at zero; enabled must reach the provider."
            ]
        };

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
        Console.WriteLine(json);
    }

    private static async Task<CompressionDynamicPolicyMeasurement> MeasureAsync(
        string state,
        IBenchmarkRpc rpc,
        byte[] payload,
        HotPathProbeCompressionProvider? clientProvider)
    {
        for (var index = 0; index < WarmupOperations; index++)
            Validate(payload, await rpc.EchoBytesAsync(payload).ConfigureAwait(false));

        var samples = new List<CompressionDynamicPolicyRound>(Rounds);
        for (var round = 0; round < Rounds; round++)
        {
            clientProvider?.Reset();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var watch = Stopwatch.StartNew();

            for (var operation = 0; operation < OperationsPerRound; operation++)
                Validate(payload, await rpc.EchoBytesAsync(payload).ConfigureAwait(false));

            watch.Stop();
            process.Refresh();
            var cpuAfter = process.TotalProcessorTime;
            var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
            samples.Add(new CompressionDynamicPolicyRound(
                allocatedBytesPerOperation: (allocatedAfter - allocatedBefore) / (double)OperationsPerRound,
                cpuMicrosecondsPerOperation: (cpuAfter - cpuBefore).TotalMilliseconds * 1000d / OperationsPerRound,
                elapsedMicrosecondsPerOperation: watch.Elapsed.TotalMilliseconds * 1000d / OperationsPerRound,
                qps: OperationsPerRound / Math.Max(watch.Elapsed.TotalSeconds, double.Epsilon),
                compressionAttemptsPerOperation: (clientProvider?.Attempts ?? 0) / (double)OperationsPerRound));
        }

        return new CompressionDynamicPolicyMeasurement
        {
            State = state,
            AllocatedBytesPerOperation = Median(samples.Select(static sample => sample.AllocatedBytesPerOperation)),
            CpuMicrosecondsPerOperation = Median(samples.Select(static sample => sample.CpuMicrosecondsPerOperation)),
            ElapsedMicrosecondsPerOperation = Median(samples.Select(static sample => sample.ElapsedMicrosecondsPerOperation)),
            Qps = Median(samples.Select(static sample => sample.Qps)),
            CompressionAttemptsPerOperation = Median(samples.Select(static sample => sample.CompressionAttemptsPerOperation))
        };
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        return ordered[ordered.Length / 2];
    }

    private static void Validate(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new InvalidOperationException("Dynamic compression evidence RPC returned an unexpected payload.");
    }

    private static void Ensure(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"Dynamic compression evidence assertion failed: {description}.");
    }

    private sealed class HotPathProbeCompressionProvider : ISharpLinkCompressionProvider
    {
        private long _attempts;

        public string WireProfile => "benchmark.dynamic-policy-probe/v1";
        public long Attempts => Interlocked.Read(ref _attempts);

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            return false;
        }

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The dynamic-policy probe never emits compressed payloads.");

        public void Reset() => Interlocked.Exchange(ref _attempts, 0);
    }

    private sealed class CompressionDynamicPolicyEvidenceDocument
    {
        public string Commit { get; init; } = string.Empty;
        public string Framework { get; init; } = string.Empty;
        public string Os { get; init; } = string.Empty;
        public string Architecture { get; init; } = string.Empty;
        public int ProcessorCount { get; init; }
        public int PayloadBytes { get; init; }
        public int OperationsPerRound { get; init; }
        public int Rounds { get; init; }
        public List<CompressionDynamicPolicyMeasurement> Measurements { get; init; } = [];
        public List<string> Notes { get; init; } = [];
    }

    private sealed class CompressionDynamicPolicyMeasurement
    {
        public string State { get; init; } = string.Empty;
        public double AllocatedBytesPerOperation { get; init; }
        public double CpuMicrosecondsPerOperation { get; init; }
        public double ElapsedMicrosecondsPerOperation { get; init; }
        public double Qps { get; init; }
        public double CompressionAttemptsPerOperation { get; init; }
    }

    private readonly record struct CompressionDynamicPolicyRound(
        double AllocatedBytesPerOperation,
        double CpuMicrosecondsPerOperation,
        double ElapsedMicrosecondsPerOperation,
        double Qps,
        double CompressionAttemptsPerOperation);
}
