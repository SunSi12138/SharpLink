using System.Net;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace DemoBase;

public static class DemoTcp
{
    public static ISharpLinkServer CreateServer<TInterface, TService>(
        int port,
        Action<SharpLinkServerBuilder>? configure = null)
        where TInterface : class, IService
        where TService : class, TInterface, new()
    {
        var builder = SharpLinkServerBuilder.Create()
            .UseTcp(port, IPAddress.Loopback.ToString())
            ;

        configure?.Invoke(builder);
        return builder.Build();
    }

    public static ISharpLinkClient CreateClient(
        int port,
        Action<SharpClientBuilder>? configure = null)
    {
        var builder = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseRequestTimeout()
            ;

        configure?.Invoke(builder);
        return builder.Build();
    }

    public static Task StartServerAsync(ISharpLinkServer server, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);
    }

    public static async Task EnsureConnectedAsync(
        ISharpLinkClient client,
        CancellationToken cancellationToken,
        string? errorMessage = null)
    {
        await client.ConnectAsync(cancellationToken);
    }

    public static async Task ShutdownAsync(
        CancellationTokenSource appCts,
        Task serverTask,
        params IAsyncDisposable?[] disposables)
    {
        appCts.Cancel();
        foreach (var disposable in disposables)
        {
            if (disposable is not null)
                await disposable.DisposeAsync();
        }

        await Task.WhenAny(serverTask, Task.Delay(300));
    }
}
