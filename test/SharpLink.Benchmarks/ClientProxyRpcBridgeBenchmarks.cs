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
/// a client-streaming RPC, and an injected-latency unary. All three bridge shapes are applied over
/// the <b>same</b> generated-proxy operation (the contract method returns <c>ValueTask&lt;T&gt;</c>):
/// <list type="bullet">
///   <item><b>Variant A</b>: <c>_rpc.Method(...).AsTask()</c> — the exact shape the generator emits
///     for a <c>Task&lt;T&gt;</c> contract method.</item>
///   <item><b>Variant B</b>: <c>async Task&lt;T&gt;</c> direct-await with <c>ConfigureAwait(false)</c>.</item>
///   <item><b>Variant C</b>: <c>ValueTask&lt;T&gt;</c> passthrough.</item>
/// </list>
/// <para>The no-result <c>ValueTask&lt;byte&gt;.AsVoid().AsTask()</c> bridge is covered at the micro
/// level by <see cref="ClientProxyBridgeBenchmarks"/>; a full-RPC byte-ack method would change the
/// wire shape (payload vs no-payload), so it is intentionally omitted here.</para>
/// <para>No generator code is modified: Variant C comes straight from the generated proxy, and
/// Variants A and B are authored in the benchmark to mirror the candidate lowering shapes.</para>
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
    public Task<int> Unary_AsTask() => _rpc.UnaryAsync(1).AsTask();

    [Benchmark]
    public async Task<int> Unary_DirectAwait()
    {
        return await _rpc.UnaryAsync(1).ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<int> Unary_ValueTask() => _rpc.UnaryAsync(1);

    // ---- client-streaming response -----------------------------------------------------

    [Benchmark]
    public Task<int> ClientStream_AsTask() => _rpc.ClientStreamAsync(_streamValues).AsTask();

    [Benchmark]
    public async Task<int> ClientStream_DirectAwait()
    {
        return await _rpc.ClientStreamAsync(_streamValues).ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<int> ClientStream_ValueTask() => _rpc.ClientStreamAsync(_streamValues);

    // ---- injected-latency unary --------------------------------------------------------

    [Benchmark]
    public Task<int> Latency_AsTask() => _rpc.LatencyAsync(1).AsTask();

    [Benchmark]
    public async Task<int> Latency_DirectAwait()
    {
        return await _rpc.LatencyAsync(1).ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<int> Latency_ValueTask() => _rpc.LatencyAsync(1);

    private static async IAsyncEnumerable<int> Values(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return i;
            await Task.CompletedTask;
        }
    }
}
