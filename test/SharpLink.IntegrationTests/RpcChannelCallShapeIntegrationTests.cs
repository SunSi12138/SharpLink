using Timeout = SharpLink.Sdk.TimeoutAttribute;

namespace SharpLink.IntegrationTests;

public class RpcChannelCallShapeIntegrationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GeneratedProxyCallsShouldWorkWithRealRpcService(bool useSharedMemory)
    {
        await using var harness = await CallShapeHarness.CreateAsync(useSharedMemory);
        var svc = harness.Client.Get<ICallShapeService>();

        Ensure(await svc.UnaryPayloadAsync(3) == 13, "UnaryPayloadAsync");
        Ensure(await svc.UnaryNoPayloadAsync() == 7, "UnaryNoPayloadAsync");
        Ensure(await svc.UnaryCancellableAsync(4, CancellationToken.None) == 14, "UnaryCancellableAsync");
        Ensure(await svc.UnaryCancellableDefaultTimeoutAsync(5, CancellationToken.None) == 15, "UnaryCancellableDefaultTimeoutAsync");
        Ensure(await svc.UnaryCancellableTimeoutAsync(6, CancellationToken.None) == 16, "UnaryCancellableTimeoutAsync");
        Ensure(await svc.UnaryDefaultTimeoutPayloadAsync(7) == 107, "UnaryDefaultTimeoutPayloadAsync");
        Ensure(await svc.UnaryDefaultTimeoutNoPayloadAsync() == 108, "UnaryDefaultTimeoutNoPayloadAsync");
        Ensure(await svc.UnaryTimeoutPayloadAsync(9) == 109, "UnaryTimeoutPayloadAsync");
        Ensure(await svc.UnaryTimeoutNoPayloadAsync() == 110, "UnaryTimeoutNoPayloadAsync");
        Ensure(await svc.UnaryCancellableNoPayloadAsync(CancellationToken.None) == 111, "UnaryCancellableNoPayloadAsync");

        await svc.VoidPayloadAsync(10);
        await svc.VoidNoPayloadAsync();
        await svc.VoidCancellableAsync(20, CancellationToken.None);
        await svc.VoidCancellableDefaultTimeoutAsync(30, CancellationToken.None);
        await svc.VoidCancellableTimeoutAsync(40, CancellationToken.None);
        await svc.VoidDefaultTimeoutPayloadAsync(50);
        await svc.VoidDefaultTimeoutNoPayloadAsync();
        await svc.VoidTimeoutPayloadAsync(60);
        await svc.VoidTimeoutNoPayloadAsync();
        await svc.VoidCancellableNoPayloadAsync(CancellationToken.None);
        await svc.VoidCancellableNoReturnWithDefaultTimeoutNoPayloadAsync(CancellationToken.None);
        await svc.VoidCancellableNoReturnWithTimeoutNoPayloadAsync(CancellationToken.None);
        Ensure(await svc.GetVoidTotalAsync() == 10 + 1 + 20 + 30 + 40 + 50 + 1 + 60 + 1 + 1 + 1 + 1, "Void totals");

        await svc.OneWayPayloadAsync(2);
        await svc.OneWayNoPayloadAsync();
        await svc.OneWayCancellableAsync(3, CancellationToken.None);
        await svc.OneWayCancellableDefaultTimeoutAsync(4, CancellationToken.None);
        await svc.OneWayCancellableTimeoutAsync(5, CancellationToken.None);
        await svc.OneWayDefaultTimeoutPayloadAsync(6);
        await svc.OneWayDefaultTimeoutNoPayloadAsync();
        await svc.OneWayTimeoutPayloadAsync(7);
        await svc.OneWayTimeoutNoPayloadAsync();
        await svc.OneWayCancellableNoPayloadAsync(CancellationToken.None);
        await svc.OneWayCancellableDefaultTimeoutNoPayloadAsync(CancellationToken.None);
        await svc.OneWayCancellableTimeoutNoPayloadAsync(CancellationToken.None);
        await EnsureEventuallyAsync(
            async () => await svc.GetOneWayTotalAsync() == 2 + 1 + 3 + 4 + 5 + 6 + 1 + 7 + 1 + 1 + 1 + 1,
            "OneWay totals");

        Ensure(await svc.ClientStreamPayloadAsync(10, ToAsyncEnumerable([1, 2, 3])) == 16, "ClientStreamPayloadAsync");
        Ensure(await svc.ClientStreamNoPayloadAsync(ToAsyncEnumerable([4, 5])) == 9, "ClientStreamNoPayloadAsync");
        await svc.ClientStreamNoReturnPayloadAsync(100, ToAsyncEnumerable([1, 2]));
        await svc.ClientStreamNoReturnNoPayloadAsync(ToAsyncEnumerable([3]));
        Ensure(await svc.ClientStreamCancellablePayloadAsync(20, ToAsyncEnumerable([1, 1]), CancellationToken.None) == 22, "ClientStreamCancellablePayloadAsync");
        Ensure(await svc.ClientStreamCancellableDefaultTimeoutPayloadAsync(30, ToAsyncEnumerable([2]), CancellationToken.None) == 32, "ClientStreamCancellableDefaultTimeoutPayloadAsync");
        Ensure(await svc.ClientStreamCancellableTimeoutPayloadAsync(40, ToAsyncEnumerable([3]), CancellationToken.None) == 43, "ClientStreamCancellableTimeoutPayloadAsync");
        Ensure(await svc.ClientStreamDefaultTimeoutPayloadAsync(50, ToAsyncEnumerable([1, 2])) == 53, "ClientStreamDefaultTimeoutPayloadAsync");
        Ensure(await svc.ClientStreamDefaultTimeoutNoPayloadAsync(ToAsyncEnumerable([3, 4])) == 7, "ClientStreamDefaultTimeoutNoPayloadAsync");
        Ensure(await svc.ClientStreamTimeoutPayloadAsync(60, ToAsyncEnumerable([1])) == 61, "ClientStreamTimeoutPayloadAsync");
        Ensure(await svc.ClientStreamTimeoutNoPayloadAsync(ToAsyncEnumerable([2, 3])) == 5, "ClientStreamTimeoutNoPayloadAsync");
        Ensure(await svc.ClientStreamCancellableNoPayloadAsync(ToAsyncEnumerable([4]), CancellationToken.None) == 4, "ClientStreamCancellableNoPayloadAsync");
        Ensure(await svc.ClientStreamCancellableWithTimeoutNoPayloadAsync(ToAsyncEnumerable([5]), CancellationToken.None) == 5, "ClientStreamCancellableWithTimeoutNoPayloadAsync");
        await svc.ClientStreamNoReturnWithDefaultTimeoutPayloadAsync(70, ToAsyncEnumerable([1]));
        await svc.ClientStreamNoReturnWithDefaultTimeoutNoPayloadAsync(ToAsyncEnumerable([2]));
        await svc.ClientStreamNoReturnWithTimeoutPayloadAsync(80, ToAsyncEnumerable([3]));
        await svc.ClientStreamNoReturnWithTimeoutNoPayloadAsync(ToAsyncEnumerable([4]));
        await svc.ClientStreamCancellableNoReturnPayloadAsync(90, ToAsyncEnumerable([1]), CancellationToken.None);
        await svc.ClientStreamCancellableNoReturnNoPayloadAsync(ToAsyncEnumerable([2]), CancellationToken.None);
        await svc.ClientStreamCancellableNoReturnWithDefaultTimeoutPayloadAsync(100, ToAsyncEnumerable([3]), CancellationToken.None);
        await svc.ClientStreamCancellableNoReturnWithDefaultTimeoutNoPayloadAsync(ToAsyncEnumerable([4]), CancellationToken.None);
        await svc.ClientStreamCancellableNoReturnWithTimeoutPayloadAsync(110, ToAsyncEnumerable([5]), CancellationToken.None);
        await svc.ClientStreamCancellableNoReturnWithTimeoutNoPayloadAsync(ToAsyncEnumerable([6]), CancellationToken.None);
        await svc.OneWayClientStreamPayloadAsync(5, ToAsyncEnumerable([1]));
        await svc.OneWayClientStreamNoPayloadAsync(ToAsyncEnumerable([2]));
        await svc.OneWayClientStreamWithDefaultTimeoutPayloadAsync(6, ToAsyncEnumerable([3]));
        await svc.OneWayClientStreamWithDefaultTimeoutNoPayloadAsync(ToAsyncEnumerable([4]));
        await svc.OneWayClientStreamWithTimeoutPayloadAsync(7, ToAsyncEnumerable([5]));
        await svc.OneWayClientStreamWithTimeoutNoPayloadAsync(ToAsyncEnumerable([6]));
        await svc.OneWayClientStreamCancellablePayloadAsync(8, ToAsyncEnumerable([7]), CancellationToken.None);
        await svc.OneWayClientStreamCancellableNoPayloadAsync(ToAsyncEnumerable([8]), CancellationToken.None);
        await svc.OneWayClientStreamCancellableWithDefaultTimeoutPayloadAsync(9, ToAsyncEnumerable([9]), CancellationToken.None);
        await svc.OneWayClientStreamCancellableWithDefaultTimeoutNoPayloadAsync(ToAsyncEnumerable([10]), CancellationToken.None);
        await svc.OneWayClientStreamCancellableWithTimeoutPayloadAsync(10, ToAsyncEnumerable([11]), CancellationToken.None);
        await svc.OneWayClientStreamCancellableWithTimeoutNoPayloadAsync(ToAsyncEnumerable([12]), CancellationToken.None);
        await EnsureEventuallyAsync(
            async () => await svc.GetClientStreamNoReturnTotalAsync() == 103 + 3 + 71 + 2 + 83 + 4 + 91 + 2 + 103 + 4 + 115 + 6 + 6 + 2 + 9 + 4 + 12 + 6 + 15 + 8 + 18 + 10 + 21 + 12,
            "ClientStreamNoReturn totals");

        Ensure((await CollectAsync(svc.ServerStreamPayloadAsync(3), CancellationToken.None)).SequenceEqual([0, 1, 2]), "ServerStreamPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamNoPayloadAsync(), CancellationToken.None)).SequenceEqual([9, 8]), "ServerStreamNoPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamCancellablePayloadAsync(2, CancellationToken.None), CancellationToken.None)).SequenceEqual([10, 11]), "ServerStreamCancellablePayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamCancellableDefaultTimeoutPayloadAsync(2, CancellationToken.None), CancellationToken.None)).SequenceEqual([20, 21]), "ServerStreamCancellableDefaultTimeoutPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamCancellableTimeoutPayloadAsync(2, CancellationToken.None), CancellationToken.None)).SequenceEqual([30, 31]), "ServerStreamCancellableTimeoutPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamDefaultTimeoutPayloadAsync(2), CancellationToken.None)).SequenceEqual([40, 41]), "ServerStreamDefaultTimeoutPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamDefaultTimeoutNoPayloadAsync(), CancellationToken.None)).SequenceEqual([42, 43]), "ServerStreamDefaultTimeoutNoPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamTimeoutPayloadAsync(2), CancellationToken.None)).SequenceEqual([44, 45]), "ServerStreamTimeoutPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamTimeoutNoPayloadAsync(), CancellationToken.None)).SequenceEqual([46, 47]), "ServerStreamTimeoutNoPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamCancellableNoPayloadAsync(CancellationToken.None), CancellationToken.None)).SequenceEqual([48, 49]), "ServerStreamCancellableNoPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamCancellableDefaultTimeoutNoPayloadAsync(CancellationToken.None), CancellationToken.None)).SequenceEqual([50, 51]), "ServerStreamCancellableDefaultTimeoutNoPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamCancellableTimeoutNoPayloadAsync(CancellationToken.None), CancellationToken.None)).SequenceEqual([52, 53]), "ServerStreamCancellableTimeoutNoPayloadAsync");

        Ensure((await CollectAsync(svc.DuplexPayloadAsync(10, ToAsyncEnumerable([1, 2])), CancellationToken.None)).SequenceEqual([11, 12]), "DuplexPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexNoPayloadAsync(ToAsyncEnumerable([3, 4])), CancellationToken.None)).SequenceEqual([3, 4]), "DuplexNoPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexCancellablePayloadAsync(20, ToAsyncEnumerable([1]), CancellationToken.None), CancellationToken.None)).SequenceEqual([21]), "DuplexCancellablePayloadAsync");
        Ensure((await CollectAsync(svc.DuplexCancellableDefaultTimeoutPayloadAsync(30, ToAsyncEnumerable([2]), CancellationToken.None), CancellationToken.None)).SequenceEqual([32]), "DuplexCancellableDefaultTimeoutPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexCancellableTimeoutPayloadAsync(40, ToAsyncEnumerable([3]), CancellationToken.None), CancellationToken.None)).SequenceEqual([43]), "DuplexCancellableTimeoutPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexDefaultTimeoutPayloadAsync(50, ToAsyncEnumerable([4])), CancellationToken.None)).SequenceEqual([54]), "DuplexDefaultTimeoutPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexDefaultTimeoutNoPayloadAsync(ToAsyncEnumerable([5])), CancellationToken.None)).SequenceEqual([5]), "DuplexDefaultTimeoutNoPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexTimeoutPayloadAsync(60, ToAsyncEnumerable([6])), CancellationToken.None)).SequenceEqual([66]), "DuplexTimeoutPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexTimeoutNoPayloadAsync(ToAsyncEnumerable([7])), CancellationToken.None)).SequenceEqual([7]), "DuplexTimeoutNoPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexCancellableNoPayloadAsync(ToAsyncEnumerable([8]), CancellationToken.None), CancellationToken.None)).SequenceEqual([8]), "DuplexCancellableNoPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexCancellableDefaultTimeoutNoPayloadAsync(ToAsyncEnumerable([9]), CancellationToken.None), CancellationToken.None)).SequenceEqual([9]), "DuplexCancellableDefaultTimeoutNoPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexCancellableTimeoutNoPayloadAsync(ToAsyncEnumerable([10]), CancellationToken.None), CancellationToken.None)).SequenceEqual([10]), "DuplexCancellableTimeoutNoPayloadAsync");
    }

    [Test]
    public async Task GeneratedProxyCancellableCallShouldThrowOperationCanceledException()
    {
        await using var harness = await CallShapeHarness.CreateAsync();
        var svc = harness.Client.Get<ICallShapeService>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

        await EnsureThrows<OperationCanceledException>(
            svc.UnaryWaitForCancellationAsync(cts.Token).AsTask(),
            "UnaryWaitForCancellationAsync");
    }

    [Test]
    public async Task GeneratedProxyTimeoutCallShouldThrowDeadlineExceeded()
    {
        await using var harness = await CallShapeHarness.CreateAsync();
        var svc = harness.Client.Get<ICallShapeService>();

        var exception = await EnsureThrows<SharpLinkException>(
            svc.UnaryAlwaysSlowWithTimeoutAsync().AsTask(),
            "UnaryAlwaysSlowWithTimeoutAsync");
        Ensure(exception.Code == SharpLinkErrorCode.DeadlineExceeded, "timeout error code");
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> stream, CancellationToken ct)
    {
        var list = new List<T>();
        await foreach (var item in stream.WithCancellation(ct))
            list.Add(item);
        return list;
    }

    private static async IAsyncEnumerable<int> ToAsyncEnumerable(IEnumerable<int> values)
    {
        foreach (var v in values)
        {
            yield return v;
            await Task.Yield();
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private static async Task EnsureEventuallyAsync(Func<Task<bool>> condition, string name)
    {
        while (!await condition())
            await Task.Delay(10);
    }

    private static async Task<TException> EnsureThrows<TException>(Task task, string name) where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"assert failed: {name}, expected {typeof(TException).Name}");
        }
        catch (TException exception)
        {
            return exception;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class CallShapeHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private bool _serverDisposed;
        private bool _clientDisposed;

        public ISharpLinkClient Client { get; }

        private CallShapeHarness(ISharpLinkServer server, Task serverTask, CancellationTokenSource serverCts, ISharpLinkClient client)
        {
            _server = server;
            _serverTask = serverTask;
            _serverCts = serverCts;
            Client = client;
        }

        public static async Task<CallShapeHarness> CreateAsync(bool useSharedMemory = false)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()

                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));

            var sharedMemoryName = $"sharplink-call-shape-{Guid.NewGuid():N}";
            var port = 0;
            if (useSharedMemory)
                serverBuilder.UseSharedMemory(sharedMemoryName);
            else
            {
                serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
                port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            }
            var server = serverBuilder.Build();

            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
            }, CancellationToken.None);

            var clientBuilder = SharpClientBuilder.Create()

                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseRequestTimeout(TimeSpan.FromSeconds(5));
            if (useSharedMemory)
                clientBuilder.UseSharedMemory(sharedMemoryName);
            else
                clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
            var client = clientBuilder.Build();

            await client.ConnectAsync(cts.Token);
            return new CallShapeHarness(server, serverTask, cts, client);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_clientDisposed)
            {
                _clientDisposed = true;
                await Client.StopAsync();
            }

            await _serverCts.CancelAsync();
            if (!_serverDisposed)
            {
                _serverDisposed = true;
                await _server.StopAsync(TimeSpan.Zero);
            }

            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }
}
