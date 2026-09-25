#if SHARPLINK_READY_WRITER_EXPERIMENT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

internal static class ReadyWriterEvidence
{
    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 9) throw new ArgumentException("<tcp|sharedmemory|pipe> <streams> <items> <bytes> <rounds> <connection-window> <ring-slots> <flush-bytes> <output>");
        int N(int i) => int.Parse(args[i], CultureInfo.InvariantCulture);
        var transport = args[0]; var streams = N(1); var items = N(2); var bytes = N(3); var rounds = N(4);
        var connection = N(5); var slots = N(6); var flush = N(7);
        if (transport is not ("tcp" or "sharedmemory" or "pipe") || streams < 1 || streams > 128 || items < 1 || bytes < 4 || bytes > 16384 || rounds < 1 || rounds > 12 || connection < 8192 || slots < 1 || slots > 256 || flush < 1)
            throw new ArgumentOutOfRangeException(nameof(args));
        var path = Path.GetFullPath(args[8]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var source = Environment.GetEnvironmentVariable("SHARPLINK_SOURCE_TREE") ?? throw new InvalidOperationException("Exact source tree required.");
        var orderOffset = Environment.GetEnvironmentVariable("SHARPLINK_READY_ORDER") == "1" ? 1 : 0;
        var samples = new List<PhaseBTransportEvidenceRunner.Sample>();
        var metadata = new ReadyWriterRunMetadata
        {
            Source = source, Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Pgo = RuntimeFeature.IsDynamicCodeSupported ? Environment.GetEnvironmentVariable("DOTNET_TieredPGO") : "nativeaot",
            DynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported, ProcessorCount = Environment.ProcessorCount,
#if SHARPLINK_READY_WRITER_DIAGNOSTIC
            DiagnosticCapture = true,
#endif
            transport = transport, streams = streams, items = items, bytes = bytes, rounds = rounds,
            connection = connection, slots = slots, flush = flush, orderOffset = orderOffset,
            preparedByteBudget = int.Parse(Environment.GetEnvironmentVariable("SHARPLINK_READY_PREPARED_BYTES") ?? "0", CultureInfo.InvariantCulture),
            allocationDiagnostic = Environment.GetEnvironmentVariable("SHARPLINK_READY_ALLOCATION_DIAGNOSTIC") == "1",
            Scope = "A-ready: complete frozen controller debits on producer; B3-ready: pump owns debit. Quanta 1/16 are matched independently; SAME bounded producer ring, actual SendPump, transport, receiver and flush. NO per-item emission waiter in EITHER. Fixed stream lifecycles and balanced updates only."
        };
        void Save(string status, string? error = null) => File.WriteAllText(path, JsonSerializer.Serialize(new ReadyWriterReport(metadata, status, error, samples), ReadyWriterJsonContext.Default.ReadyWriterReport));
        try
        {
            var variants = new[] { (Mode: "A-ready", Quantum: 1), (Mode: "B3-ready", Quantum: 1), (Mode: "A-ready", Quantum: 16), (Mode: "B3-ready", Quantum: 16) };
            foreach (var variant in orderOffset == 0 ? variants : System.Linq.Enumerable.Reverse(variants))
            {
                var timer = Stopwatch.StartNew(); var passes = 0;
                do { await PhaseBTransportCase.RunAsync(variant.Mode, transport, streams, Math.Min(items, 512), bytes, -1, connection, slots, flush, variant.Quantum); passes++; }
                while (passes < 2 || timer.ElapsedMilliseconds < 1000);
            }
            for (var round = 0; round < rounds; round++)
                for (var position = 0; position < variants.Length; position++)
                {
                    var v = variants[(round / 2 + orderOffset * 2 + (round % 2 == 0 ? position : 3 - position)) % 4];
                    var sample = await PhaseBTransportCase.RunAsync(v.Mode, transport, streams, items, bytes, round, connection, slots, flush, v.Quantum);
                    sample = sample with { Mode = $"{v.Mode}/q{v.Quantum}" };
                    samples.Add(sample); Save("in_progress");
                    Console.WriteLine($"{sample.Mode} {transport} c{streams}/{bytes} W{connection} slots{slots} flush{flush} r{round}: {sample.ItemsPerSecond:F0} item/s, {sample.CpuMs:F1} CPU-ms, {sample.AllocatedBytesPerItem:F3} B/item");
                }
            Save("completed");
        }
        catch (Exception error) { Save("failed", error.ToString()); throw; }
    }
}
#endif
