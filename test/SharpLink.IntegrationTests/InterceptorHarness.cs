namespace SharpLink.IntegrationTests;

internal sealed class InterceptorHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _serverCts;
    private readonly Task _serverTask;
    public ISharpLinkServer Server { get; }
    public ISharpLinkClient Client { get; }
    public RpcProxy<IInterceptorTestService> Proxy { get; }
    public IInterceptorTestService Service => Proxy.Service;

    private InterceptorHarness(
        CancellationTokenSource serverCts,
        Task serverTask,
        ISharpLinkServer server,
        ISharpLinkClient client,
        RpcProxy<IInterceptorTestService> proxy)
    {
        _serverCts = serverCts;
        _serverTask = serverTask;
        Server = server;
        Client = client;
        Proxy = proxy;
    }

    public static async Task<InterceptorHarness> CreateAsync(
        ISharpLinkClientInterceptor? clientInterceptor = null,
        ISharpLinkServerInterceptor? serverInterceptor = null,
        IRpcExceptionMapper? exceptionMapper = null,
        bool enableDetailedErrors = false,
        TimeSpan? requestTimeout = null,
        ISharpLinkRetryPolicy? retryPolicy = null,
        bool prepare = true)
    {
        var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
        if (serverInterceptor is not null)
            serverBuilder.AddInterceptor(serverInterceptor);
        if (exceptionMapper is not null)
            serverBuilder.UseExceptionMapper(exceptionMapper);
        if (enableDetailedErrors)
            serverBuilder.EnableDetailedErrors();

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(() => server.RunUntilStoppedAsync(cts.Token).AsTask(), CancellationToken.None);

        var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
        if (clientInterceptor is not null)
            clientBuilder.AddInterceptor(clientInterceptor);
        if (requestTimeout is { } timeout)
            clientBuilder.UseRequestTimeout(timeout);
        if (retryPolicy is not null)
            clientBuilder.UseRetry(retryPolicy);
        var client = clientBuilder.Build();
        try
        {
            await client.ConnectAsync(cts.Token);
            var proxy = client.CreateProxy<IInterceptorTestService>();
            if (prepare)
                await proxy.PrepareAsync(TimeSpan.FromSeconds(5), cts.Token);
            return new InterceptorHarness(cts, serverTask, server, client, proxy);
        }
        catch
        {
            await DisposeAsync(client, serverTask, server, cts);
            throw;
        }
    }

    public ValueTask DisposeAsync() => DisposeAsync(Client, _serverTask, Server, _serverCts);

    private static async ValueTask DisposeAsync(
        ISharpLinkClient client, Task serverTask, ISharpLinkServer server, CancellationTokenSource serverCts)
    {
        using (serverCts)
        {
            try
            {
                await client.DisposeAsync();
            }
            finally
            {
                await serverCts.CancelAsync();
                await server.DisposeAsync();
                await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
            }
        }
    }
}
