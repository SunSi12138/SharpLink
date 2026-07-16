using Timeout = SharpLink.Sdk.TimeoutAttribute;

namespace SharpLink.IntegrationTests;

public class RpcChannelCallShapeIntegrationTests
{
    [Test]
    public async Task GeneratedProxyCallsShouldWorkWithRealRpcService()
    {
        await using var harness = await CallShapeHarness.CreateAsync();
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
        var deadline = TimeProvider.System.GetTimestamp() + TimeProvider.System.TimestampFrequency;

        while (TimeProvider.System.GetTimestamp() < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(10);
        }

        Ensure(await condition(), name);
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

        public static async Task<CallShapeHarness> CreateAsync()
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .AddService<ICallShapeService, CallShapeService>()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));

            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
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

            var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseRequestTimeout(TimeSpan.FromSeconds(5))
                .Build();

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

[RpcContract]
public interface ICallShapeService : IService
{
    ValueTask<int> UnaryPayloadAsync(int payload);
    ValueTask<int> UnaryNoPayloadAsync();
    ValueTask<int> UnaryCancellableAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask<int> UnaryDefaultTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask<int> UnaryDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> UnaryTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> UnaryTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    ValueTask<int> UnaryCancellableNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask<int> UnaryCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> UnaryCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    ValueTask<int> UnaryWaitForCancellationAsync(CancellationToken cancellationToken = default);
    [Timeout(0.2)]
    ValueTask<int> UnaryAlwaysSlowWithTimeoutAsync(CancellationToken cancellationToken = default);

    ValueTask VoidPayloadAsync(int payload);
    ValueTask VoidNoPayloadAsync();
    ValueTask VoidCancellableAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask VoidDefaultTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask VoidDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask VoidTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask VoidTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    ValueTask VoidCancellableNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask VoidCancellableNoReturnWithDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask VoidCancellableNoReturnWithTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask VoidCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask VoidCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    ValueTask<int> GetVoidTotalAsync();

    [Oneway]
    ValueTask OneWayPayloadAsync(int payload);
    [Oneway]
    ValueTask OneWayNoPayloadAsync();
    [Oneway]
    ValueTask OneWayCancellableAsync(int payload, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayDefaultTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Oneway]
    ValueTask OneWayCancellableNoPayloadAsync(CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayCancellableDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayCancellableTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    ValueTask<int> GetOneWayTotalAsync();

    ValueTask<int> ClientStreamPayloadAsync(int marker, IAsyncEnumerable<int> stream);
    ValueTask<int> ClientStreamNoPayloadAsync(IAsyncEnumerable<int> stream);
    [Timeout]
    ValueTask<int> ClientStreamDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask<int> ClientStreamDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> ClientStreamTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> ClientStreamTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    ValueTask<int> ClientStreamCancellableNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> ClientStreamCancellableWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    ValueTask ClientStreamNoReturnPayloadAsync(int marker, IAsyncEnumerable<int> stream);
    ValueTask ClientStreamNoReturnNoPayloadAsync(IAsyncEnumerable<int> stream);
    [Timeout]
    ValueTask ClientStreamNoReturnWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask ClientStreamNoReturnWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask ClientStreamNoReturnWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask ClientStreamNoReturnWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    ValueTask<int> ClientStreamCancellablePayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask<int> ClientStreamCancellableDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> ClientStreamCancellableTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    ValueTask ClientStreamCancellableNoReturnPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    ValueTask ClientStreamCancellableNoReturnNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask ClientStreamCancellableNoReturnWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask ClientStreamCancellableNoReturnWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask ClientStreamCancellableNoReturnWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask ClientStreamCancellableNoReturnWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);

    [Oneway]
    ValueTask OneWayClientStreamPayloadAsync(int marker, IAsyncEnumerable<int> stream);
    [Oneway]
    ValueTask OneWayClientStreamNoPayloadAsync(IAsyncEnumerable<int> stream);
    [Oneway]
    [Timeout]
    ValueTask OneWayClientStreamWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayClientStreamWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayClientStreamWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayClientStreamWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    ValueTask OneWayClientStreamCancellablePayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    ValueTask OneWayClientStreamCancellableNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayClientStreamCancellableWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayClientStreamCancellableWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayClientStreamCancellableWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayClientStreamCancellableWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);

    ValueTask<int> GetClientStreamNoReturnTotalAsync();

    IAsyncEnumerable<int> ServerStreamPayloadAsync(int count);
    IAsyncEnumerable<int> ServerStreamNoPayloadAsync();
    [Timeout]
    IAsyncEnumerable<int> ServerStreamDefaultTimeoutPayloadAsync(int count, CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> ServerStreamDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> ServerStreamTimeoutPayloadAsync(int count, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> ServerStreamTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<int> ServerStreamCancellablePayloadAsync(int count, CancellationToken cancellationToken = default);
    IAsyncEnumerable<int> ServerStreamCancellableNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> ServerStreamCancellableDefaultTimeoutPayloadAsync(int count, CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> ServerStreamCancellableDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> ServerStreamCancellableTimeoutPayloadAsync(int count, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> ServerStreamCancellableTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<int> DuplexPayloadAsync(int add, IAsyncEnumerable<int> stream);
    IAsyncEnumerable<int> DuplexNoPayloadAsync(IAsyncEnumerable<int> stream);
    [Timeout]
    IAsyncEnumerable<int> DuplexDefaultTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> DuplexDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> DuplexTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> DuplexTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    IAsyncEnumerable<int> DuplexCancellablePayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    IAsyncEnumerable<int> DuplexCancellableNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> DuplexCancellableDefaultTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> DuplexCancellableDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> DuplexCancellableTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> DuplexCancellableTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
}

[RpcService]
public sealed class CallShapeService : ICallShapeService
{
    private int _voidTotal;
    private int _oneWayTotal;
    private int _clientStreamNoReturnTotal;

    public ValueTask<int> UnaryPayloadAsync(int payload) => ValueTask.FromResult(payload + 10);
    public ValueTask<int> UnaryNoPayloadAsync() => ValueTask.FromResult(7);
    public ValueTask<int> UnaryCancellableAsync(int payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(payload + 10);
    public ValueTask<int> UnaryDefaultTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(payload + 100);
    public ValueTask<int> UnaryDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(108);
    public ValueTask<int> UnaryTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(payload + 100);
    public ValueTask<int> UnaryTimeoutNoPayloadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(110);
    public ValueTask<int> UnaryNonCancellableDefaultTimeoutPayloadAsync(int payload) => ValueTask.FromResult(payload + 200);
    public ValueTask<int> UnaryNonCancellableDefaultTimeoutNoPayloadAsync() => ValueTask.FromResult(208);
    public ValueTask<int> UnaryNonCancellableTimeoutPayloadAsync(int payload) => ValueTask.FromResult(payload + 200);
    public ValueTask<int> UnaryNonCancellableTimeoutNoPayloadAsync() => ValueTask.FromResult(210);
    public ValueTask<int> UnaryCancellableNoPayloadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(111);
    public ValueTask<int> UnaryCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(payload + 10);
    public ValueTask<int> UnaryCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(payload + 10);
    public async ValueTask<int> UnaryWaitForCancellationAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(global::System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
    public async ValueTask<int> UnaryAlwaysSlowWithTimeoutAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
        return 1;
    }

    public ValueTask VoidPayloadAsync(int payload)
    {
        Interlocked.Add(ref _voidTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidNoPayloadAsync()
    {
        Interlocked.Increment(ref _voidTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidCancellableAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _voidTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidDefaultTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _voidTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _voidTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _voidTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _voidTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidCancellableNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _voidTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidCancellableNoReturnWithDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _voidTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidCancellableNoReturnWithTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _voidTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _voidTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _voidTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> GetVoidTotalAsync() => ValueTask.FromResult(_voidTotal);

    public ValueTask OneWayPayloadAsync(int payload)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayNoPayloadAsync()
    {
        Interlocked.Increment(ref _oneWayTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayDefaultTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _oneWayTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _oneWayTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _oneWayTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _oneWayTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _oneWayTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> GetOneWayTotalAsync() => ValueTask.FromResult(_oneWayTotal);

    public async ValueTask<int> ClientStreamPayloadAsync(int marker, IAsyncEnumerable<int> stream)
        => marker + await SumAsync(stream);

    public async ValueTask<int> ClientStreamNoPayloadAsync(IAsyncEnumerable<int> stream)
        => await SumAsync(stream).ConfigureAwait(false);

    public async ValueTask<int> ClientStreamDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamCancellableNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamCancellableWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => await SumAsync(stream, cancellationToken);

    public async ValueTask ClientStreamNoReturnPayloadAsync(int marker, IAsyncEnumerable<int> stream)
    {
        var sum = await SumAsync(stream);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask ClientStreamNoReturnNoPayloadAsync(IAsyncEnumerable<int> stream)
    {
        var sum = await SumAsync(stream);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask ClientStreamNoReturnWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask ClientStreamNoReturnWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask ClientStreamNoReturnWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask ClientStreamNoReturnWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask<int> ClientStreamCancellablePayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamCancellableDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamCancellableTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

    public async ValueTask ClientStreamCancellableNoReturnPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask ClientStreamCancellableNoReturnNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask ClientStreamCancellableNoReturnWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask ClientStreamCancellableNoReturnWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask ClientStreamCancellableNoReturnWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask ClientStreamCancellableNoReturnWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask OneWayClientStreamPayloadAsync(int marker, IAsyncEnumerable<int> stream)
    {
        var sum = await SumAsync(stream);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask OneWayClientStreamNoPayloadAsync(IAsyncEnumerable<int> stream)
    {
        var sum = await SumAsync(stream);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask OneWayClientStreamWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask OneWayClientStreamWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask OneWayClientStreamWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask OneWayClientStreamWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask OneWayClientStreamCancellablePayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask OneWayClientStreamCancellableNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask OneWayClientStreamCancellableWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask OneWayClientStreamCancellableWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask OneWayClientStreamCancellableWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask OneWayClientStreamCancellableWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public ValueTask<int> GetClientStreamNoReturnTotalAsync() => ValueTask.FromResult(_clientStreamNoReturnTotal);

    public async IAsyncEnumerable<int> ServerStreamPayloadAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return i;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> ServerStreamNoPayloadAsync()
    {
        yield return 9;
        await Task.Yield();
        yield return 8;
    }

    public async IAsyncEnumerable<int> ServerStreamDefaultTimeoutPayloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            yield return 40 + i;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> ServerStreamDefaultTimeoutNoPayloadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return 42;
        await Task.Yield();
        yield return 43;
    }

    public async IAsyncEnumerable<int> ServerStreamTimeoutPayloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            yield return 44 + i;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> ServerStreamTimeoutNoPayloadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return 46;
        await Task.Yield();
        yield return 47;
    }

    public async IAsyncEnumerable<int> ServerStreamCancellablePayloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return 10 + i;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> ServerStreamCancellableNoPayloadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return 48;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return 49;
    }

    public async IAsyncEnumerable<int> ServerStreamCancellableDefaultTimeoutPayloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return 20 + i;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> ServerStreamCancellableDefaultTimeoutNoPayloadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return 50;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return 51;
    }

    public async IAsyncEnumerable<int> ServerStreamCancellableTimeoutPayloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return 30 + i;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> ServerStreamCancellableTimeoutNoPayloadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return 52;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return 53;
    }

    public async IAsyncEnumerable<int> DuplexPayloadAsync(int add, IAsyncEnumerable<int> stream)
    {
        await foreach (var item in stream)
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexNoPayloadAsync(IAsyncEnumerable<int> stream)
    {
        await foreach (var item in stream)
            yield return item;
    }

    public async IAsyncEnumerable<int> DuplexDefaultTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<int> DuplexTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<int> DuplexCancellablePayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexCancellableNoPayloadAsync(IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<int> DuplexCancellableDefaultTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexCancellableDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<int> DuplexCancellableTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexCancellableTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    private static async Task<int> SumAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = 0;
        await foreach (var v in stream.WithCancellation(cancellationToken))
            sum += v;
        return sum;
    }
}
