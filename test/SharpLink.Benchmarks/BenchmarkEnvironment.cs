using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
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
    private static readonly TimeSpan s_prepareTimeout = TimeSpan.FromSeconds(10);
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
            .UseTcp(0, IPAddress.Loopback);
        if (configureServerRuntime is not null)
            serverBuilder.UseRuntime(configureServerRuntime);
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
        if (configureClientRuntime is not null)
            client.UseRuntime(configureClientRuntime);
        var builtClient = client.Build();
        try
        {
            configureBuiltClient?.Invoke(builtClient);
            await builtClient.ConnectAsync(shutdown.Token).ConfigureAwait(false);
            await WaitForReadyConnectionsAsync(builtClient, expectedReadyConnections, shutdown.Token).ConfigureAwait(false);
            var rpc = await PrepareServiceAsync<IBenchmarkRpc>(builtClient, shutdown.Token,
                balancePreparedEndpoints: expectedReadyConnections > 1).ConfigureAwait(false);
            return new BenchmarkEnvironment(rpc, localService, shutdown, serverTask, server, builtClient);
        }
        catch
        {
            await DisposeFailedSetupAsync(builtClient, server, shutdown, serverTask).ConfigureAwait(false);
            throw;
        }
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
        if (configureServerRuntime is not null)
            serverBuilder.UseRuntime(configureServerRuntime);
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
        if (configureClientRuntime is not null)
            clientBuilder.UseRuntime(configureClientRuntime);
        var client = clientBuilder.Build();
        try
        {
            configureBuiltClient?.Invoke(client);
            await client.ConnectAsync(shutdown.Token).ConfigureAwait(false);
            var rpc = await PrepareServiceAsync<IBenchmarkRpc>(client, shutdown.Token).ConfigureAwait(false);
            return new BenchmarkEnvironment(rpc, localService, shutdown, serverTask, server, client, serverReadProbe);
        }
        catch
        {
            await DisposeFailedSetupAsync(client, server, shutdown, serverTask).ConfigureAwait(false);
            throw;
        }
    }

    internal ISharpLinkClient Client => _client;
    internal bool HasPendingServerRead => _serverReadProbe?.HasPendingRead ?? false;
    internal ISharpLinkServer Server => _server;

    // Establish the binding during setup, before the benchmark starts recording.
    internal static async ValueTask<TContract> PrepareServiceAsync<TContract>(
        ISharpLinkClient client, CancellationToken cancellationToken, bool balancePreparedEndpoints = false)
        where TContract : IService
    {
        var proxy = client.CreateProxy<TContract>(new RpcProxyOptions
        {
            BalancePreparedEndpoints = balancePreparedEndpoints
        });
        await proxy.PrepareAsync(s_prepareTimeout, cancellationToken).ConfigureAwait(false);
        return proxy.Service;
    }

    internal ValueTask<TContract> PrepareServiceAsync<TContract>() where TContract : IService
        => PrepareServiceAsync<TContract>(_client, _shutdown.Token);

    private static async Task DisposeFailedSetupAsync(
        ISharpLinkClient client, ISharpLinkServer server, CancellationTokenSource shutdown, Task serverTask)
    {
        using (shutdown)
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await server.DisposeAsync().ConfigureAwait(false);
                await Task.WhenAny(serverTask, Task.Delay(500)).ConfigureAwait(false);
            }
        }
    }

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
