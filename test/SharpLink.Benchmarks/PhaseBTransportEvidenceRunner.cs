using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SharpLink.Benchmarks;

// Same-path control, not generated RPC throughput: each producer retains exactly
// one unsettled emission; the real transport, frame parsing, receive accounting
// and peer credit return are included on BOTH sides of every comparison.
internal static partial class PhaseBTransportEvidenceRunner
{

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length is not (7 or 8))
            throw new ArgumentException("Usage: <tcp|sharedmemory|pipe> <streams> <items> <item-bytes> <rounds> <connection-window> <output.json> [A|B1|B2|B2-adaptive]");
        var transport = args[0];
        if (transport is not ("tcp" or "sharedmemory" or "pipe")) throw new ArgumentException("Unknown transport.");
        int Read(int index) => int.Parse(args[index], CultureInfo.InvariantCulture);
        var streams = Read(1); var items = Read(2); var bytes = Read(3); var rounds = Read(4); var connectionWindow = Read(5);
        if (streams < 1 || streams > 128 || items < 1 || bytes < 4 || bytes > 4096 || rounds < 1 || rounds > 12 || connectionWindow < 8192)
            throw new ArgumentOutOfRangeException(nameof(args));
        var modes = args.Length == 8 ? new[] { args[7] } : new[] { "A", "B1", "B2", "B2-adaptive" };
        if (modes.Any(mode => mode is not ("A" or "B1" or "B2" or "B2-adaptive"))) throw new ArgumentException("Unknown mode.");
        var samples = new List<Sample>();
        var output = Path.GetFullPath(args[6]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var metadata = new
        {
            Source = Environment.GetEnvironmentVariable("SHARPLINK_SOURCE_TREE") ?? "unrecorded",
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Pgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
            ProcessorCount = Environment.ProcessorCount,
#if SHARPLINK_READY_WRITER_DIAGNOSTIC
            DiagnosticCapture = true,
#endif
            Scope = "Same 1-unsettled-emission-per-stream pacing; setup/handshake excluded. Actual transport, data validation, production receive accounting and balanced key-only credits included. No generated RPC dispatch, duplicate-credit compatibility or production Go claim.",
            transport,
            streams,
            items,
            bytes,
            rounds,
            connectionWindow
        };
        void Save(string status, string? error = null) => File.WriteAllText(output,
            JsonSerializer.Serialize(new { metadata, status, error, samples }, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            // Warm the real path, not a simplified operation. Warmup is never a sample.
            foreach (var mode in modes)
            {
                Console.WriteLine($"warmup {transport} {mode} c{streams}");
                var warm = Stopwatch.StartNew();
                var passes = 0;
                do
                {
                    _ = await PhaseBTransportCase.RunAsync(mode, transport, streams, Math.Min(items, 512), bytes, -1, connectionWindow);
                    passes++;
                } while (passes < 2 || warm.Elapsed < TimeSpan.FromSeconds(1));
            }
            for (var round = 0; round < rounds; round++)
            {
                // Cyclic/reverse order; eight rounds balance every mode across all positions.
                var order = modes.Length == 1 ? modes : Enumerable.Range(0, modes.Length)
                    .Select(i => modes[(round / 2 + (round % 2 == 0 ? i : modes.Length - 1 - i)) % modes.Length]).ToArray();
                foreach (var mode in order)
                {
                    var sample = await PhaseBTransportCase.RunAsync(mode, transport, streams, items, bytes, round, connectionWindow);
                    samples.Add(sample);
                    Save("in_progress");
                    Console.WriteLine($"{transport} {mode} c{streams} size{bytes} r{round}: {sample.ItemsPerSecond:F0} item/s, {sample.CpuMs:F2} CPU-ms, {sample.AllocatedBytesPerItem:F2} B/item, {sample.OwnerCommandsPerItem:F6} owner/item");
                }
            }
            Save("completed");
        }
        catch (Exception error)
        {
            Save("failed", error.ToString());
            throw;
        }
    }
}
