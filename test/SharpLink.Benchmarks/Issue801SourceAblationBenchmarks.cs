using System;
using System.Reflection;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

/// <summary>
/// #801 BDN source-ablation probe. Compares actual default production code
/// to the same code with selected test-only branches physically removed
/// in a temporary CI worktree, never in dev.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 12)]
public class Issue801AdmissionAblationBenchmarks
{
    private BenchmarkEnvironment _environment = null!;
    private SharpLinkServer _server = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var ablated = Environment.GetEnvironmentVariable("SHARPLINK_ABLATION_VARIANT") == "stripped";
        foreach (var fieldName in new[]
        {
            "s_afterAdmissionCaptureForTests",
            "s_afterAdmissionPublicationReadForTests"
        })
        {
            var field = typeof(SharpLinkServer).GetField(
                fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            if ((field is null) != ablated)
                throw new InvalidOperationException(
                    $"Wrong source variant: {fieldName}, present={field is not null}, stripped={ablated}");
            if (field is not null && field.GetValue(null) is not null)
                throw new InvalidOperationException("Test hooks must remain null for default-path measurement.");
        }

        _environment = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
                server.EnableAdmissionControl(options => options.Global.UseConcurrency(1024)));
        _server = (SharpLinkServer)_environment.Server;
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _environment.DisposeAsync();

    [Benchmark]
    public long CaptureAndRelease()
    {
        var program = _server.CaptureAdmissionProgramForTests(42)
            ?? throw new InvalidOperationException("Admission must remain enabled.");
        try { return program.GenerationId; }
        finally { program.ReleaseUse(); }
    }
}

/// <summary>
/// #801 ablates one Client instance test-only delegate field and six test-only
/// handshake awaits, preserving builder and production connection semantics.
/// This case does not connect to a network.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 12)]
public class Issue801ClientAblationBenchmarks
{
    [GlobalSetup]
    public void VerifyVariant()
    {
        var ablated = Environment.GetEnvironmentVariable("SHARPLINK_ABLATION_VARIANT") == "stripped";
        var field = typeof(SharpLinkClient).GetField(
            "_beforeReadyPublicationTestHook", BindingFlags.Instance | BindingFlags.NonPublic);
        if ((field is null) != ablated)
            throw new InvalidOperationException(
                $"Wrong Client source variant, present={field is not null}, stripped={ablated}");
    }

    [Benchmark]
    public async Task BuildAndDisposeDefaultAsync()
    {
        var client = SharpClientBuilder.Create()
            .UseTcp("127.0.0.1", 1)
            .DisableRequestTimeout()
            .Build();
        await client.DisposeAsync().ConfigureAwait(false);
    }
}
