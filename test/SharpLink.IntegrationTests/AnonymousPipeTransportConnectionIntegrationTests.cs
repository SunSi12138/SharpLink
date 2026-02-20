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
        harness.DisposeServerOnly();

        await EnsureThrowsAnyFast(pending, "anonymous pipe pending should fail fast after server dispose");
    }

    [Test]
    public async Task AnonymousPipeClientDisposeShouldFailFastPendingCall()
    {
        await using var harness = await AnonymousPipeHarness.CreateAsync();
        var svc = harness.Client.Get<IConnectionBehaviorService>();
        Ensure(await svc.PingAsync(2) == 3, "anonymous pipe warmup ping");

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        harness.DisposeClientOnly();

        await EnsureThrowsAnyFast(pending, "anonymous pipe pending should fail fast after client dispose");
    }

    private static async Task EnsureThrowsAnyFast(Task task, string name)
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
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            IgnoreExpectedException(ex);
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
                .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
                .UseAnonymousPipe()
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

            var server = serverBuilder.Build();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.Start(cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
                {
                    IgnoreExpectedException(ex);
                }
            }, CancellationToken.None);

            var allocator = (IAnonymousPipeAllocator)serverBuilder.Transport!;
            var (inHandle, outHandle) = allocator.AllocateNewSession();

            var client = SharpClientBuilder.Create()
                .UseAnonymousPipe(inHandle, outHandle)
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
                .Build();

            var connected = await client.ConnectAsync(cts.Token);
            if (!connected)
                throw new Exception("client connect failed");

            return new AnonymousPipeHarness(server, serverTask, cts, client);
        }

        public void DisposeServerOnly()
        {
            if (_serverDisposed)
                return;

            _serverDisposed = true;
            try
            {
                (_server as IDisposable)?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                IgnoreExpectedException(ex);
            }
        }

        public void DisposeClientOnly()
        {
            if (_clientDisposed)
                return;

            _clientDisposed = true;
            try
            {
                (Client as IDisposable)?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                IgnoreExpectedException(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            DisposeClientOnly();
            try
            {
                await _serverCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            DisposeServerOnly();
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
