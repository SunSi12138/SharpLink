using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

internal static class JitEvidenceRunner
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 3)
        {
            throw new ArgumentException(
                "Usage: --jit-evidence <server|client> <iterations> <output-json>");
        }

        var component = args[0].ToLowerInvariant();
        var iterations = int.Parse(args[1], CultureInfo.InvariantCulture);
        var outputPath = Path.GetFullPath(args[2]);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);

        var scenarios = component switch
        {
            "server" => await ExerciseServerAsync(iterations).ConfigureAwait(false),
            "client" => await ExerciseClientAsync(iterations).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(component),
                component,
                "Expected server or client.")
        };

        var document = new JitEvidenceDocument
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Component = component,
            IterationsPerScenario = iterations,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
            TieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default",
            Scenarios = scenarios
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
        Console.WriteLine(json);
    }

    private static async Task<IReadOnlyList<string>> ExerciseServerAsync(int iterations)
    {
        await using var benchmark = await FeatureBenchmarkCase.CreateAsync(
            ServerFeatureScenario.AdmissionImmediate).ConfigureAwait(false);
        for (var index = 0; index < iterations; index++)
        {
            Validate(await benchmark.InvokeAsync().ConfigureAwait(false));
            await benchmark.InvokeOneWayAsync().ConfigureAwait(false);
        }

        // A response received after the final one-way send proves the receive loop
        // consumed every earlier frame before teardown.
        Validate(await benchmark.InvokeAsync().ConfigureAwait(false));
        return ["AdmissionImmediateUnary", "AdmissionImmediateOneWay"];
    }

    private static async Task<IReadOnlyList<string>> ExerciseClientAsync(int iterations)
    {
        await using (var fixedClient = await FeatureBenchmarkCase.CreateAsync(
                         ClientFeatureScenario.FixedDefault).ConfigureAwait(false))
        {
            for (var index = 0; index < iterations; index++)
                Validate(await fixedClient.InvokeAsync().ConfigureAwait(false));
        }

        await using (var retryClient = await FeatureBenchmarkCase.CreateAsync(
                         ClientFeatureScenario.RetryFirstSuccess).ConfigureAwait(false))
        {
            for (var index = 0; index < iterations; index++)
                Validate(await retryClient.InvokeAsync().ConfigureAwait(false));
        }

        return ["FixedDefault", "RetryFirstSuccess"];
    }

    private static void Validate(int result)
    {
        if (result != 30)
            throw new InvalidOperationException($"JIT evidence RPC returned {result} instead of 30.");
    }
}

internal sealed class JitEvidenceDocument
{
    public string Commit { get; init; } = string.Empty;
    public string Component { get; init; } = string.Empty;
    public int IterationsPerScenario { get; init; }
    public string TieredCompilation { get; init; } = string.Empty;
    public string TieredPgo { get; init; } = string.Empty;
    public IReadOnlyList<string> Scenarios { get; init; } = [];
}
