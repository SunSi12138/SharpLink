using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal sealed class BenchmarkEnvironment : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown;
    private readonly Task _serverTask;
    private readonly ISharpLinkServer _server;
    private readonly ISharpLinkClient _client;
    private readonly AllocationReadProbe? _serverReadProbe;

    public IBenchmarkRpc Rpc { get; }
    public BenchmarkRpcService LocalService { get; }

    private BenchmarkEnvironment(
        IBenchmarkRpc rpc,
        BenchmarkRpcService localService,
        CancellationTokenSource shutdown,
        Task serverTask,
        ISharpLinkServer server,
        ISharpLinkClient client,
        AllocationReadProbe? serverReadProbe = null)
    {
        Rpc = rpc;
        LocalService = localService;
        _shutdown = shutdown;
        _serverTask = serverTask;
        _server = server;
        _client = client;
        _serverReadProbe = serverReadProbe;
    }

    // This is benchmark-only, and intentionally also compiles against the fixed dev
    // baseline, which has no writer-owned switch. A request for an unsupported mode
    // fails immediately rather than silently benchmarking legacy code.
    private static Action<SharpLinkRuntimeOptions>? WithWriterOwnedBenchmarkSelection(
        Action<SharpLinkRuntimeOptions>? original)
    {
        if (Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_WRITER_OWNED") != "1")
            return original;
        return options =>
        {
            original?.Invoke(options);
            var selection = typeof(SharpLinkFlowControlOptions).GetProperty(
                "WriterOwnedStreams", BindingFlags.Instance | BindingFlags.NonPublic);
            if (selection is null || selection.PropertyType != typeof(bool))
                throw new NotSupportedException("This revision cannot enable the B3 sender.");
            selection.SetValue(options.FlowControl, true);
            if (selection.GetValue(options.FlowControl) is not true)
                throw new InvalidOperationException("Benchmark B3 selection was not enabled.");
        };
    }

    // Check the actual negotiated session, not only the mutable builder flag.
    // Without this check a successful run could accidentally measure legacy code.
    internal void AssertWriterOwnedBenchmarkMode()
    {
        if (Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_WRITER_OWNED") != "1")
            return;
        if (_client is not SharpLinkClient client)
            throw new InvalidOperationException("The benchmark did not create a SharpLink client.");
        var ready = typeof(SharpLinkClient).GetField("_readyConnections",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as Array;
        if (ready is null || ready.Length == 0)
            throw new InvalidOperationException("No connected benchmark session to verify.");
        foreach (var connection in ready)
        {
            var session = connection!.GetType().GetProperty("Session",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(connection);
            var selected = session?.GetType().GetProperty("UsesWriterOwnedStreams",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(session);
            if (selected is not true)
                throw new InvalidOperationException("B3 was requested but the connected session uses legacy credit.");
        }
    }

    public static async Task<BenchmarkEnvironment> CreateAsync(
        Action<SharpLinkServerBuilder>? configureServer = null,
        Action<SharpLinkRuntimeOptions>? configureServerRuntime = null,
        Action<SharpLinkRuntimeOptions>? configureClientRuntime = null,
        Func<int, SharpClientBuilder>? createClientBuilder = null,
        Action<ISharpLinkServer>? configureBuiltServer = null,
        Action<ISharpLinkClient>? configureBuiltClient = null,
        int expectedReadyConnections = 1)
    {
        var localService = new BenchmarkRpcService();

        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString());
        var serverRuntimeConfiguration = WithWriterOwnedBenchmarkSelection(configureServerRuntime);
        if (serverRuntimeConfiguration is not null)
            serverBuilder.UseRuntime(serverRuntimeConfiguration);
        configureServer?.Invoke(serverBuilder);

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        configureBuiltServer?.Invoke(server);

        var shutdown = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunUntilStoppedAsync(shutdown.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, shutdown.Token);

        var client = createClientBuilder?.Invoke(port) ?? SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port);
        client.DisableRequestTimeout();
        var clientRuntimeConfiguration = WithWriterOwnedBenchmarkSelection(configureClientRuntime);
        if (clientRuntimeConfiguration is not null)
            client.UseRuntime(clientRuntimeConfiguration);
        var builtClient = client.Build();
        configureBuiltClient?.Invoke(builtClient);

        await builtClient.ConnectAsync(shutdown.Token);
        await WaitForReadyConnectionsAsync(
            builtClient,
            expectedReadyConnections,
            shutdown.Token).ConfigureAwait(false);

        var rpc = builtClient.Get<IBenchmarkRpc>();
        return new BenchmarkEnvironment(
            rpc, localService, shutdown, serverTask, server, builtClient);
    }

    public static async Task<BenchmarkEnvironment> CreateSharedMemoryAsync(
        Action<SharpLinkRuntimeOptions>? configureServerRuntime = null,
        Action<SharpLinkRuntimeOptions>? configureClientRuntime = null,
        Action<ISharpLinkServer>? configureBuiltServer = null,
        Action<ISharpLinkClient>? configureBuiltClient = null,
        bool observeServerReadWaits = false)
    {
        var name = $"sharplink-allocation-{Guid.NewGuid():N}";
        var localService = new BenchmarkRpcService();
        var serverReadProbe = observeServerReadWaits ? new AllocationReadProbe() : null;
        var transport = new SharedMemoryServerTransportListener(name);
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTransport(serverReadProbe?.Wrap(transport) ?? transport)
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        var serverRuntimeConfiguration = WithWriterOwnedBenchmarkSelection(configureServerRuntime);
        if (serverRuntimeConfiguration is not null)
            serverBuilder.UseRuntime(serverRuntimeConfiguration);
        serverBuilder.ReplaceService<IBenchmarkRpc>(localService);
        var server = serverBuilder.Build();
        configureBuiltServer?.Invoke(server);
        var shutdown = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunUntilStoppedAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);

        var clientBuilder = SharpClientBuilder.Create()
            .UseSharedMemory(name)
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        clientBuilder.DisableRequestTimeout();
        var clientRuntimeConfiguration = WithWriterOwnedBenchmarkSelection(configureClientRuntime);
        if (clientRuntimeConfiguration is not null)
            clientBuilder.UseRuntime(clientRuntimeConfiguration);
        var client = clientBuilder.Build();
        configureBuiltClient?.Invoke(client);
        try
        {
            await client.ConnectAsync(shutdown.Token).ConfigureAwait(false);
            return new BenchmarkEnvironment(
                client.Get<IBenchmarkRpc>(), localService, shutdown, serverTask, server, client, serverReadProbe);
        }
        catch
        {
            shutdown.Cancel();
            await client.DisposeAsync().ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            shutdown.Dispose();
            throw;
        }
    }

    internal ISharpLinkClient Client => _client;
    internal bool HasPendingServerRead => _serverReadProbe?.HasPendingRead ?? false;
    internal ISharpLinkServer Server => _server;

    public TContract Get<TContract>() where TContract : class, IService => _client.Get<TContract>();

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _client.StopAsync();
        await _server.StopAsync(TimeSpan.Zero);
        await Task.WhenAny(_serverTask, Task.Delay(500));
        _shutdown.Dispose();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForReadyConnectionsAsync(
        ISharpLinkClient client,
        int expected,
        CancellationToken cancellationToken)
    {
        if (expected <= 1)
            return;

        var concrete = (SharpLinkClient)client;
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (concrete.ReadyConnectionCount < expected)
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException(
                    $"Only {concrete.ReadyConnectionCount} of {expected} benchmark connections became ready.");
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async IAsyncEnumerable<T> ToStream<T>(
        IReadOnlyList<T> values,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var t in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return t;
            await Task.CompletedTask;
        }
    }
}
