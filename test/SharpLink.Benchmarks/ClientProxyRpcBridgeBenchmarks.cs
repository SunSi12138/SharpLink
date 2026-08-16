using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

/// <summary>
/// End-to-end confirmation of the issue #159 client-proxy bridge over a tiny loopback unary RPC,
/// a no-result RPC, a client-streaming RPC, and an injected-latency unary. Each logical call is
/// exercised in all three bridge shapes:
/// <list type="bullet">
///   <item><b>Variant A</b>: the generated proxy's <c>.AsTask()</c> (contract returns <c>Task</c>/<c>Task&lt;T&gt;</c>).</item>
///   <item><b>Variant B</b>: an <c>async Task</c> direct-await over the <c>ValueTask</c> shape.</item>
///   <item><b>Variant C</b>: the generated proxy's <c>ValueTask</c> passthrough.</item>
/// </list>
/// <para>No generator code is modified: Variant A and C come straight from the generated proxy,
/// Variant B is authored in the benchmark to mirror the candidate lowering shape.</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 1,
    warmupCount: 3,
    invocationCount: 2048,
    iterationCount: 10)]
public class ClientProxyRpcBridgeBenchmarks
{
    private CancellationTokenSource _shutdown = null!;
    private Task _serverTask = null!;
    private ISharpLinkServer _server = null!;
    private ISharpLinkClient _client = null!;
    private IClientBridgeRpc _rpc = null!;
    private IAsyncEnumerable<int> _streamValues = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString());
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        _server = serverBuilder.Build();

        _shutdown = new CancellationTokenSource();
        _serverTask = Task.Run(async () =>
        {
            try
            {
                await _server.RunAsync(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, _shutdown.Token);

        _client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .Build();
        await _client.ConnectAsync(_shutdown.Token);
        _rpc = _client.Get<IClientBridgeRpc>();
        _streamValues = Values(16);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _shutdown.Cancel();
        await _client.StopAsync();
        await _server.StopAsync(TimeSpan.Zero);
        await Task.WhenAny(_serverTask, Task.Delay(500));
        _shutdown.Dispose();
    }

    // ---- tiny unary RPC ----------------------------------------------------------------

    [Benchmark(Baseline = true)]
    public Task<int> Unary_AsTask() => _rpc.UnaryTaskAsync(1);

    [Benchmark]
    public async Task<int> Unary_DirectAwait()
    {
        return await _rpc.UnaryValueTaskAsync(1).ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<int> Unary_ValueTask() => _rpc.UnaryValueTaskAsync(1);

    // ---- no-result RPC -----------------------------------------------------------------

    [Benchmark]
    public Task NoResult_AsTask() => _rpc.UnaryNoResultTaskAsync(1);

    [Benchmark]
    public async Task NoResult_DirectAwait()
    {
        await _rpc.UnaryNoResultValueTaskAsync(1).ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask NoResult_ValueTask() => _rpc.UnaryNoResultValueTaskAsync(1);

    // ---- client-streaming response -----------------------------------------------------

    [Benchmark]
    public Task<int> ClientStream_AsTask() => _rpc.ClientStreamTaskAsync(_streamValues);

    [Benchmark]
    public async Task<int> ClientStream_DirectAwait()
    {
        return await _rpc.ClientStreamValueTaskAsync(_streamValues).ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<int> ClientStream_ValueTask() => _rpc.ClientStreamValueTaskAsync(_streamValues);

    // ---- injected-latency unary --------------------------------------------------------

    [Benchmark]
    public Task<int> Latency_AsTask() => _rpc.LatencyTaskAsync(1);

    [Benchmark]
    public async Task<int> Latency_DirectAwait()
    {
        return await _rpc.LatencyValueTaskAsync(1).ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<int> Latency_ValueTask() => _rpc.LatencyValueTaskAsync(1);

    private static async IAsyncEnumerable<int> Values(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return i;
            await Task.CompletedTask;
        }
    }
}
