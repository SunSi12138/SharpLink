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
                BatchSize = 4096,
                Boundary = "Sender diagnostic batches with a validated existing Rpc_Add between batches; NOT a BDN score or OneWay receipt.",
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
        const int batchSize = 4096;
        for (var offset = 0; offset < count; offset += batchSize)
        {
            Phases.Log.Phase("batch-start");
            var end = Math.Min(count, offset + batchSize);
            for (var i = offset; i < end; i++)
                await benchmark.Rpc_OnewayPublish();
            Phases.Log.Phase("batch-stop");
            // An explicit diagnostic boundary, not a receipt or a change to OneWay.
            // Keep the original queue limit and allow its existing send pump to run.
            if (await benchmark.Rpc_Add() != 30)
                throw new InvalidOperationException("Diagnostic batch boundary failed.");
        }
    }
}

[EventSource(Name = "SharpLink-Issue775-Attribution")]
internal sealed class Phases : EventSource
{
    internal static readonly Phases Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Phase(string name) => WriteEvent(1, name);
}
