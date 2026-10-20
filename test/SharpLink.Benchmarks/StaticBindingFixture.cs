using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

// Same harness in both arms. Only explicit Bind preparation is experimental.
internal sealed class StaticBindingFixture : IAsyncDisposable
{
    internal SharpLinkClient Client { get; }
    internal ISharpLinkServer Server { get; }
    internal IBenchmarkRpc Rpc { get; }
    internal StaticBindingObserver Receiver { get; }
    internal double BindMilliseconds { get; private set; }
    internal double FirstCallMicroseconds { get; private set; }

    private StaticBindingFixture(SharpLinkClient client, ISharpLinkServer server,
        IBenchmarkRpc rpc, StaticBindingObserver receiver)
        => (Client, Server, Rpc, Receiver) = (client, server, rpc, receiver);

    internal static async Task<StaticBindingFixture> CreateAsync(string transport, int pool, bool prepare = true)
    {
        var receiver = new StaticBindingObserver();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        var clientBuilder = SharpClientBuilder.Create()
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
            .DisableRequestTimeout()
            .UseConnectionPool(options => { options.MinConnections = pool; options.MaxConnections = pool; });
        if (transport == "tcp")
        {
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(),
                ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port);
        }
        else if (transport == "sharedmemory")
        {
            var name = $"sharplink-static-binding-{Guid.NewGuid():N}";
            serverBuilder.UseTransport(new SharedMemoryServerTransportListener(name));
            clientBuilder.UseSharedMemory(name);
        }
        else throw new ArgumentException("Expected tcp or sharedmemory.", nameof(transport));
        serverBuilder.ReplaceService<IBenchmarkRpc>(receiver);
        var server = serverBuilder.Build();
        var client = (SharpLinkClient)clientBuilder.Build();
        try
        {
#if SHARPLINK_STATIC_BINDING_PROTOTYPE
            // Creating a proxy before Connect is deliberately part of every experimental fixture.
            var proxy = client.Get<IBenchmarkRpc>();
#endif
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await server.StartAsync(budget.Token);
            await client.ConnectAsync(budget.Token);
            while (client.ReadyConnectionCount != pool)
                await Task.Delay(1, budget.Token);
#if !SHARPLINK_STATIC_BINDING_PROTOTYPE
            var proxy = client.Get<IBenchmarkRpc>();
#endif
            var result = new StaticBindingFixture(client, server, proxy, receiver);
            if (prepare) await result.PrepareAsync(budget.Token);
            return result;
        }
        catch
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            throw;
        }
    }

    internal async Task PrepareAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
#if SHARPLINK_STATIC_BINDING_PROTOTYPE
        await Client.PrepareStaticBindingAsync<IBenchmarkRpc>(cancellationToken);
#else
        cancellationToken.ThrowIfCancellationRequested();
#endif
        BindMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        StaticBindingEvidence.Require(await Rpc.AddAsync(20, 22) == 42, "First call response");
        FirstCallMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
    }
}

// Instrumentation is intentionally identical in A/B. The OneWay timestamp is supplied by the
// sender as existing business data; there is no acknowledgement on the wire.
internal sealed class StaticBindingObserver : BenchmarkRpcService, IBenchmarkRpc
{
    private long _completed;
    private long _lastCompletion;
    private long[] _latencies = [];
    private int[] _seen = [];
    internal long Completed => Volatile.Read(ref _completed);
    internal long LastCompletion => Volatile.Read(ref _lastCompletion);
    internal long[] Latencies => _latencies;
    internal int[] Seen => _seen;
    internal void Reset(int operations)
    {
        _latencies = new long[operations];
        _seen = new int[operations];
        _completed = 0;
        _lastCompletion = 0;
    }

    ValueTask<int> IBenchmarkRpc.AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    ValueTask IBenchmarkRpc.PublishEventAsync(int code, long ticks, string tag)
    {
        StaticBindingEvidence.Require(tag == "binding" && (uint)code < (uint)_latencies.Length, "Receiver payload");
        StaticBindingEvidence.Require(Interlocked.Exchange(ref _seen[code], 1) == 0, "Duplicate OneWay operation");
        _latencies[code] = Stopwatch.GetTimestamp() - ticks;
        if (Interlocked.Increment(ref _completed) == _latencies.Length)
            Volatile.Write(ref _lastCompletion, Stopwatch.GetTimestamp());
        return ValueTask.CompletedTask;
    }
}
