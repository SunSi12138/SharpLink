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

        await svc.VoidPayloadAsync(10);
        await svc.VoidNoPayloadAsync();
        await svc.VoidCancellableAsync(20, CancellationToken.None);
        await svc.VoidCancellableDefaultTimeoutAsync(30, CancellationToken.None);
        await svc.VoidCancellableTimeoutAsync(40, CancellationToken.None);
        Ensure(await svc.GetVoidTotalAsync() == 10 + 1 + 20 + 30 + 40, "Void totals");

        await svc.OneWayPayloadAsync(2);
        await svc.OneWayNoPayloadAsync();
        await svc.OneWayCancellableAsync(3, CancellationToken.None);
        await svc.OneWayCancellableDefaultTimeoutAsync(4, CancellationToken.None);
        await svc.OneWayCancellableTimeoutAsync(5, CancellationToken.None);
        await Task.Delay(100);
        Ensure(await svc.GetOneWayTotalAsync() == 2 + 1 + 3 + 4 + 5, "OneWay totals");

        Ensure(await svc.ClientStreamPayloadAsync(10, ToAsyncEnumerable([1, 2, 3])) == 16, "ClientStreamPayloadAsync");
        Ensure(await svc.ClientStreamNoPayloadAsync(ToAsyncEnumerable([4, 5])) == 9, "ClientStreamNoPayloadAsync");
        await svc.ClientStreamNoReturnPayloadAsync(100, ToAsyncEnumerable([1, 2]));
        await svc.ClientStreamNoReturnNoPayloadAsync(ToAsyncEnumerable([3]));
        Ensure(await svc.ClientStreamCancellablePayloadAsync(20, ToAsyncEnumerable([1, 1]), CancellationToken.None) == 22, "ClientStreamCancellablePayloadAsync");
        Ensure(await svc.ClientStreamCancellableDefaultTimeoutPayloadAsync(30, ToAsyncEnumerable([2]), CancellationToken.None) == 32, "ClientStreamCancellableDefaultTimeoutPayloadAsync");
        Ensure(await svc.ClientStreamCancellableTimeoutPayloadAsync(40, ToAsyncEnumerable([3]), CancellationToken.None) == 43, "ClientStreamCancellableTimeoutPayloadAsync");
        Ensure(await svc.GetClientStreamNoReturnTotalAsync() == 103 + 3, "ClientStreamNoReturn totals");

        Ensure((await CollectAsync(svc.ServerStreamPayloadAsync(3), CancellationToken.None)).SequenceEqual([0, 1, 2]), "ServerStreamPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamNoPayloadAsync(), CancellationToken.None)).SequenceEqual([9, 8]), "ServerStreamNoPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamCancellablePayloadAsync(2, CancellationToken.None), CancellationToken.None)).SequenceEqual([10, 11]), "ServerStreamCancellablePayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamCancellableDefaultTimeoutPayloadAsync(2, CancellationToken.None), CancellationToken.None)).SequenceEqual([20, 21]), "ServerStreamCancellableDefaultTimeoutPayloadAsync");
        Ensure((await CollectAsync(svc.ServerStreamCancellableTimeoutPayloadAsync(2, CancellationToken.None), CancellationToken.None)).SequenceEqual([30, 31]), "ServerStreamCancellableTimeoutPayloadAsync");

        Ensure((await CollectAsync(svc.DuplexPayloadAsync(10, ToAsyncEnumerable([1, 2])), CancellationToken.None)).SequenceEqual([11, 12]), "DuplexPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexNoPayloadAsync(ToAsyncEnumerable([3, 4])), CancellationToken.None)).SequenceEqual([3, 4]), "DuplexNoPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexCancellablePayloadAsync(20, ToAsyncEnumerable([1]), CancellationToken.None), CancellationToken.None)).SequenceEqual([21]), "DuplexCancellablePayloadAsync");
        Ensure((await CollectAsync(svc.DuplexCancellableDefaultTimeoutPayloadAsync(30, ToAsyncEnumerable([2]), CancellationToken.None), CancellationToken.None)).SequenceEqual([32]), "DuplexCancellableDefaultTimeoutPayloadAsync");
        Ensure((await CollectAsync(svc.DuplexCancellableTimeoutPayloadAsync(40, ToAsyncEnumerable([3]), CancellationToken.None), CancellationToken.None)).SequenceEqual([43]), "DuplexCancellableTimeoutPayloadAsync");
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
            var port = GetFreePort();
            var cts = new CancellationTokenSource();
            var server = SharpLinkServerBuilder.Create()
                .AddService<ICallShapeService, CallShapeService>()
                .UseTcp(port, IPAddress.Loopback.ToString())
                .UseSerializer(new MemoryPackSerializerAdaptor())
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
                .Build();

            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.Start(cts.Token);
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
                .UseSerializer(new MemoryPackSerializerAdaptor())
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
                .UseRequestTimeout(TimeSpan.FromSeconds(5))
                .Build();

            var connected = await client.ConnectAsync();
            if (!connected)
                throw new Exception("client connect failed");

            return new CallShapeHarness(server, serverTask, cts, client);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_clientDisposed)
            {
                _clientDisposed = true;
                (Client as IDisposable)?.Dispose();
            }

            await _serverCts.CancelAsync();
            if (!_serverDisposed)
            {
                _serverDisposed = true;
                (_server as IDisposable)?.Dispose();
            }

            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }
}

public interface ICallShapeService : IService
{
    ValueTask<int> UnaryPayloadAsync(int payload);
    ValueTask<int> UnaryNoPayloadAsync();
    ValueTask<int> UnaryCancellableAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask<int> UnaryCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> UnaryCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default);

    ValueTask VoidPayloadAsync(int payload);
    ValueTask VoidNoPayloadAsync();
    ValueTask VoidCancellableAsync(int payload, CancellationToken cancellationToken = default);
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
    ValueTask OneWayCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    ValueTask<int> GetOneWayTotalAsync();

    ValueTask<int> ClientStreamPayloadAsync(int marker, IAsyncEnumerable<int> stream);
    ValueTask<int> ClientStreamNoPayloadAsync(IAsyncEnumerable<int> stream);
    ValueTask ClientStreamNoReturnPayloadAsync(int marker, IAsyncEnumerable<int> stream);
    ValueTask ClientStreamNoReturnNoPayloadAsync(IAsyncEnumerable<int> stream);
    ValueTask<int> ClientStreamCancellablePayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask<int> ClientStreamCancellableDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> ClientStreamCancellableTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    ValueTask<int> GetClientStreamNoReturnTotalAsync();

    IAsyncEnumerable<int> ServerStreamPayloadAsync(int count);
    IAsyncEnumerable<int> ServerStreamNoPayloadAsync();
    IAsyncEnumerable<int> ServerStreamCancellablePayloadAsync(int count, CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> ServerStreamCancellableDefaultTimeoutPayloadAsync(int count, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> ServerStreamCancellableTimeoutPayloadAsync(int count, CancellationToken cancellationToken = default);

    IAsyncEnumerable<int> DuplexPayloadAsync(int add, IAsyncEnumerable<int> stream);
    IAsyncEnumerable<int> DuplexNoPayloadAsync(IAsyncEnumerable<int> stream);
    IAsyncEnumerable<int> DuplexCancellablePayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> DuplexCancellableDefaultTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> DuplexCancellableTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
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
    public ValueTask<int> UnaryCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(payload + 10);
    public ValueTask<int> UnaryCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(payload + 10);

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

    public ValueTask OneWayCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> GetOneWayTotalAsync() => ValueTask.FromResult(_oneWayTotal);

    public async ValueTask<int> ClientStreamPayloadAsync(int marker, IAsyncEnumerable<int> stream)
        => marker + await SumAsync(stream);

    public async ValueTask<int> ClientStreamNoPayloadAsync(IAsyncEnumerable<int> stream)
        => await SumAsync(stream);

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

    public async ValueTask<int> ClientStreamCancellablePayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamCancellableDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamCancellableTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

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

    public async IAsyncEnumerable<int> ServerStreamCancellablePayloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return 10 + i;
            await Task.Yield();
        }
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

    public async IAsyncEnumerable<int> ServerStreamCancellableTimeoutPayloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return 30 + i;
            await Task.Yield();
        }
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

    public async IAsyncEnumerable<int> DuplexCancellablePayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexCancellableDefaultTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexCancellableTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    private static async Task<int> SumAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = 0;
        await foreach (var v in stream.WithCancellation(cancellationToken))
            sum += v;
        return sum;
    }
}
