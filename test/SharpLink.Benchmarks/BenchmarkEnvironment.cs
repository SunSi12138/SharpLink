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
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal sealed class BenchmarkEnvironment : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown;
    private readonly Task _serverTask;
    private readonly IDisposable? _serverDisposable;
    private readonly IDisposable? _clientDisposable;

    public IBenchmarkRpc Rpc { get; }
    public BenchmarkRpcService LocalService { get; }

    private BenchmarkEnvironment(
        IBenchmarkRpc rpc,
        BenchmarkRpcService localService,
        CancellationTokenSource shutdown,
        Task serverTask,
        IDisposable? serverDisposable,
        IDisposable? clientDisposable)
    {
        Rpc = rpc;
        LocalService = localService;
        _shutdown = shutdown;
        _serverTask = serverTask;
        _serverDisposable = serverDisposable;
        _clientDisposable = clientDisposable;
    }

    public static async Task<BenchmarkEnvironment> CreateAsync()
    {
        var localService = new BenchmarkRpcService();

        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IBenchmarkRpc, BenchmarkRpcService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver);

        var port = ((IPEndPoint)((ILocalEndPointTransport)serverBuilder.Transport!).LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var shutdown = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.Start(shutdown.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, shutdown.Token);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .Build();

        var connected = await client.ConnectAsync(shutdown.Token);
        if (!connected)
            throw new InvalidOperationException("Failed to connect benchmark client.");

        var rpc = client.Get<IBenchmarkRpc>();
        return new BenchmarkEnvironment(
            rpc,
            localService,
            shutdown,
            serverTask,
            server as IDisposable,
            client as IDisposable);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();

        _clientDisposable?.Dispose();
        _serverDisposable?.Dispose();

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
