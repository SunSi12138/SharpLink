using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

/// <summary>
/// Measures the server-level call-admission hot path used by #368.
/// The benchmark intentionally calls the stable SharpLinkServer entry points so the same source can
/// be copied to the current dev baseline and compare the pre-extraction implementation with the PR
/// merge result on the same runner.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 15)]
public class ServerCallAdmissionHotPathBenchmarks
{
    private BenchmarkEnvironment _environment = null!;
    private SharpLinkServer _server = null!;
    private ServerConnectionState _connection = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _environment = await BenchmarkEnvironment.CreateAsync(
            configureServerRuntime: options =>
            {
                options.FlowControl.MaxConcurrentCallsPerConnection = 1_024;
                options.FlowControl.MaxConcurrentCallsPerServer = 1_024;
            });

        _server = (SharpLinkServer)_environment.Server;
        var registry = typeof(SharpLinkServer).GetField(
                "_connectionRegistry",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_server) as ServerConnectionRegistry
            ?? throw new InvalidOperationException("Cannot resolve benchmark connection registry.");
        _connection = registry.SnapshotActive().Single();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _environment.DisposeAsync();

    // Exercise GlobalSetup and a balanced admission without a full BenchmarkDotNet measurement run.
    internal static async Task RunSmokeTestAsync()
    {
        var benchmark = new ServerCallAdmissionHotPathBenchmarks();
        await benchmark.Setup();
        try
        {
            if (benchmark.AcquireAndRelease() != 0)
                throw new InvalidOperationException("Call-admission benchmark did not release its capacity.");
        }
        finally
        {
            await benchmark.Cleanup();
        }
    }

    [Benchmark]
    public int AcquireAndRelease()
    {
        var result = _server.TryAcquireCall(_connection);
        if (result != ServerCallAdmissionResult.Acquired)
            throw new InvalidOperationException($"Unexpected admission result: {result}.");

        _server.ReleaseCall(_connection);
        return _server.ActiveCallCountForDiagnostics;
    }
}
