using System.Collections.Concurrent;
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

        _server = (SharpLinkServer)(typeof(BenchmarkEnvironment).GetField(
            "_server",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_environment)
            ?? throw new InvalidOperationException("Cannot resolve benchmark server."));
        var connections = (ConcurrentDictionary<string, ServerConnectionState>)(
            typeof(SharpLinkServer).GetField(
                "_connections",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_server)
            ?? throw new InvalidOperationException("Cannot resolve benchmark connection table."));
        _connection = connections.Values.Single();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _environment.DisposeAsync();

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
