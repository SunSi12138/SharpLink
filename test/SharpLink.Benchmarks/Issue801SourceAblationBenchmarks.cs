using System;
using System.Reflection;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Client;
using SharpLink.Abstractions;
using SharpLink.Runtime;
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


/// <summary>Real loopback TCP Protocol v2 request/response with Server admission enabled.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 4, iterationCount: 12)]
public class Issue801WireRpcAblationBenchmarks
{
    private BenchmarkEnvironment _environment = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _environment = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
                server.EnableAdmissionControl(options => options.Global.UseConcurrency(1024)));
        if (await _environment.Rpc.AddAsync(1, 41).ConfigureAwait(false) != 42)
            throw new InvalidOperationException("A real loopback RPC did not return the expected result.");
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _environment.DisposeAsync();

    [Benchmark]
    public ValueTask<int> AddViaRealTcpAsync() => _environment.Rpc.AddAsync(1, 41);
}

/// <summary>
/// Repeated real TCP transport loss, followed by the actual fixed-client reconnect
/// owner re-establishing a negotiated Protocol v2 session and completing an RPC.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3,
    invocationCount: 16, iterationCount: 10)]
public class Issue801ReconnectAblationBenchmarks
{
    private BenchmarkEnvironment _environment = null!;
    private MonitoredClientTransportFactory _factory = null!;
    private SharpLinkClient _client = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _environment = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
                server.EnableAdmissionControl(options => options.Global.UseConcurrency(1024)),
            createClientBuilder: port =>
            {
                _factory = new MonitoredClientTransportFactory(
                    new SocketClientTransportFactory(new IPEndPoint(IPAddress.Loopback, port)));
                return SharpClientBuilder.Create()
                    .UseTransport(_factory)
                    .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
                    .UseReconnectPolicy(new SharpLinkReconnectPolicy(
                        initialDelay: TimeSpan.FromMilliseconds(2),
                        maxBackoff: TimeSpan.FromMilliseconds(2),
                        backoffMultiplier: 1d,
                        jitterMinimumFactor: 1d,
                        jitterMaximumFactor: 1d,
                        stableResetWindow: TimeSpan.Zero));
            });
        _client = (SharpLinkClient)_environment.Client;
        if (_factory.ConnectCount != 1 || _client.ReadyConnectionCount != 1 ||
            await _environment.Rpc.AddAsync(10, 32).ConfigureAwait(false) != 42)
        {
            throw new InvalidOperationException("Real fixed-client initial connection failed.");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _environment.DisposeAsync();

    [Benchmark]
    public async Task<int> ForceTransportLossAndReconnectAsync()
    {
        var previousCount = _factory.ConnectCount;
        var previous = _factory.LastConnection ??
            throw new InvalidOperationException("No active connected TCP transport.");
        await previous.DisposeAsync().ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (_factory.ConnectCount <= previousCount ||
               _client.ReadyConnectionCount != 1)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), timeout.Token).ConfigureAwait(false);
        }
        var sum = await _environment.Rpc.AddAsync(10, 32).ConfigureAwait(false);
        if (sum != 42)
            throw new InvalidOperationException("Reconnected TCP RPC returned an invalid result.");
        return sum;
    }
}

internal sealed class MonitoredClientTransportFactory(IClientTransportFactory inner)
    : IClientTransportFactory
{
    private ITransportConnection? _lastConnection;
    private int _connectCount;

    internal int ConnectCount => Volatile.Read(ref _connectCount);
    internal ITransportConnection? LastConnection => Volatile.Read(ref _lastConnection);

    public async ValueTask<ITransportConnection> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        var connected = await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _lastConnection, connected);
        Interlocked.Increment(ref _connectCount);
        return connected;
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
