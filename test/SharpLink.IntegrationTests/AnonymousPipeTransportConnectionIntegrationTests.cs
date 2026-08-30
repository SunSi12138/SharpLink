namespace SharpLink.IntegrationTests;

public class AnonymousPipeTransportConnectionIntegrationTests
{
    [Test]
    public async Task AnonymousPipeConnectAndBasicRpcShouldWork()
    {
        await using var harness = await AnonymousPipeHarness.CreateAsync();
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var value = await svc.PingAsync(13);
        Ensure(value == 14, "anonymous pipe ping");
    }

    [Test]
    public async Task AnonymousPipeServerUnexpectedDisconnectShouldFailFastPendingCall()
    {
        await using var harness = await AnonymousPipeHarness.CreateAsync();
        var svc = harness.Client.Get<IConnectionBehaviorService>();
        Ensure(await svc.PingAsync(1) == 2, "anonymous pipe warmup ping");

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeServerOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "anonymous pipe pending should fail fast after server dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task AnonymousPipeClientDisposeShouldFailFastPendingCall()
    {
        await using var harness = await AnonymousPipeHarness.CreateAsync();
        var svc = harness.Client.Get<IConnectionBehaviorService>();
        Ensure(await svc.PingAsync(2) == 3, "anonymous pipe warmup ping");

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeClientOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "anonymous pipe pending should fail fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    private static async Task EnsureThrowsSharpLinkFast(Task task, string name, SharpLinkErrorCode errorCode)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception($"assert failed: {name} should throw");
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {name} did not fail fast");
        }
        catch (SharpLinkException ex)
        {
            Ensure(ex.Code == errorCode, $"{name} error code");
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private sealed class AnonymousPipeHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private bool _serverDisposed;
        private bool _clientDisposed;

        public ISharpLinkClient Client { get; }

        private AnonymousPipeHarness(
            ISharpLinkServer server,
            Task serverTask,
            CancellationTokenSource serverCts,
            ISharpLinkClient client)
        {
            _server = server;
            _serverTask = serverTask;
            _serverCts = serverCts;
            Client = client;
        }

        public static async Task<AnonymousPipeHarness> CreateAsync()
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseAnonymousPipe()

                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

            var allocator = (IAnonymousPipeAllocator)serverBuilder.Transport!;
            var (inHandle, outHandle) = await allocator.AllocateAsync(cts.Token);

            var client = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseAnonymousPipe(inHandle, outHandle)

                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
                .Build();

            var server = serverBuilder.Build();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
                {
                    IgnoreExpectedException(ex);
                }
            }, CancellationToken.None);

            await client.ConnectAsync(cts.Token);

            return new AnonymousPipeHarness(server, serverTask, cts, client);
        }

        public async ValueTask DisposeServerOnlyAsync()
        {
            if (_serverDisposed)
                return;

            _serverDisposed = true;
            try
            {
                await _server.StopAsync(TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                IgnoreExpectedException(ex);
            }
        }

        public async ValueTask DisposeClientOnlyAsync()
        {
            if (_clientDisposed)
                return;

            _clientDisposed = true;
            try
            {
                await Client.StopAsync();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                IgnoreExpectedException(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeClientOnlyAsync();
            try
            {
                await _serverCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            await DisposeServerOnlyAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            try
            {
                _serverCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static void IgnoreExpectedException(Exception ex)
    {
        _ = ex.HResult;
    }
}
