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
    private readonly CancellationTokenSource _shutdown;
    private readonly Task _serverTask;
    private readonly ISharpLinkServer _server;
    private readonly ISharpLinkClient _client;

    public IBenchmarkRpc Rpc { get; }
    public BenchmarkRpcService LocalService { get; }

    private BenchmarkEnvironment(
        IBenchmarkRpc rpc,
        BenchmarkRpcService localService,
        CancellationTokenSource shutdown,
        Task serverTask,
        ISharpLinkServer server,
        ISharpLinkClient client)
    {
        Rpc = rpc;
        LocalService = localService;
        _shutdown = shutdown;
        _serverTask = serverTask;
        _server = server;
        _client = client;
    }

    public static async Task<BenchmarkEnvironment> CreateAsync(
        Action<SharpLinkServerBuilder>? configureServer = null,
        Action<SharpLinkRuntimeOptions>? configureServerRuntime = null,
        Action<SharpLinkRuntimeOptions>? configureClientRuntime = null)
    {
        var localService = new BenchmarkRpcService();

        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            ;
        if (configureServerRuntime is not null)
            serverBuilder.UseRuntime(configureServerRuntime);
        configureServer?.Invoke(serverBuilder);

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var shutdown = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(shutdown.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, shutdown.Token);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            ;
        if (configureClientRuntime is not null)
            client.UseRuntime(configureClientRuntime);
        var builtClient = client.Build();

        await builtClient.ConnectAsync(shutdown.Token);

        var rpc = builtClient.Get<IBenchmarkRpc>();
        return new BenchmarkEnvironment(
            rpc,
            localService,
            shutdown,
            serverTask,
            server,
            builtClient);
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
