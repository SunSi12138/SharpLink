using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal sealed class BenchmarkEnvironment : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown;
    private readonly ISharpLinkServer _server;
    private readonly ISharpLinkClient _client;

    public IBenchmarkRpc Rpc { get; }

    private BenchmarkEnvironment(
        IBenchmarkRpc rpc,
        CancellationTokenSource shutdown,
        ISharpLinkServer server,
        ISharpLinkClient client)
    {
        Rpc = rpc;
        _shutdown = shutdown;
        _server = server;
        _client = client;
    }

    public static async Task<BenchmarkEnvironment> CreateAsync(
        Func<int, SharpClientBuilder>? createClientBuilder = null)
    {
        var service = new BenchmarkRpcService();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .ReplaceService<IBenchmarkRpc>(service);
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var shutdown = new CancellationTokenSource();

        await server.StartAsync(shutdown.Token).ConfigureAwait(false);

        var clientBuilder = createClientBuilder?.Invoke(port) ??
            SharpClientBuilder.Create().UseTcp(IPAddress.Loopback.ToString(), port);
        clientBuilder.DisableRequestTimeout();
        var client = clientBuilder.Build();

        try
        {
            await client.ConnectAsync(shutdown.Token).ConfigureAwait(false);
            return new BenchmarkEnvironment(
                client.Get<IBenchmarkRpc>(),
                shutdown,
                server,
                client);
        }
        catch
        {
            shutdown.Cancel();
            await client.DisposeAsync().ConfigureAwait(false);
            await server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            shutdown.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            await _client.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await _server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
        }
        finally
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _shutdown.Dispose();
        }
    }
}
