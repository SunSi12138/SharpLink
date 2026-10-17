using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Benchmarks;

namespace SharpLink.Issue775.Attribution;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Expected output.json");
        var benchmark = new UnaryBenchmarks { PayloadSize = 256 };
        await benchmark.Setup();
        try
        {
            Phases.Log.Phase("warmup-start");
            await ExerciseAsync(benchmark, 10000);
            Phases.Log.Phase("steady-start");
            var started = Stopwatch.GetTimestamp();
            await ExerciseAsync(benchmark, 1000000);
            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            Phases.Log.Phase("steady-stop");
            await File.WriteAllTextAsync(args[0], JsonSerializer.Serialize(new
            {
                Completed = 1000000,
                Magic = (int)ProtocolV2Constants.Magic,
                Seconds = seconds,
                Boundary = "Sender completion only; diagnostic direct call, NOT a BDN score.",
                Expression = "Unmodified UnaryBenchmarks.Rpc_OnewayPublish; PayloadSize is only its existing setup label."
            }));
        }
        finally
        {
            Phases.Log.Phase("cleanup-start");
            await benchmark.Cleanup();
        }
    }

    private static async Task ExerciseAsync(UnaryBenchmarks benchmark, int count)
    {
        for (var i = 0; i < count; i++)
            await benchmark.Rpc_OnewayPublish();
    }
}

[EventSource(Name = "SharpLink-Issue775-Attribution")]
internal sealed class Phases : EventSource
{
    internal static readonly Phases Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Phase(string name) => WriteEvent(1, name);
}
