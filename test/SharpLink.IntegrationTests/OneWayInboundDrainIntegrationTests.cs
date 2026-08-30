namespace SharpLink.IntegrationTests;

public class OneWayInboundDrainIntegrationTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [NotInParallel]
    public async Task ShortCircuitedOneWayClientStreamShouldDrainBeyondWindowAndKeepConnectionUsable()
    {
        CompressionService.ResetOneWay();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
        });
        var interceptor = new ShortCircuitOneWayServerInterceptor();
        harness.Server.ReplaceInterceptors([interceptor]);
        try
        {
            var service = harness.Client.Get<ICompressionService>();
            var send = service.NotifyStreamBytesAsync(ManyPayloads(128, 32)).AsTask();

            await interceptor.Entered.WaitAsync(PhaseTimeout);
            await Task.Delay(75);
            Ensure(!send.IsCompleted,
                "sender should exhaust the small receive window while the OneWay invocation is still gated");

            interceptor.Release();
            await send.WaitAsync(PhaseTimeout);
            await Task.Delay(50);

            Ensure(!CompressionService.WaitForOneWayAsync().IsCompleted,
                "short-circuited OneWay invocation must not reach the generated service method");
            var probe = Enumerable.Repeat((byte)0x5a, 32).ToArray();
            var echoed = await service.EchoBytesAsync(probe).AsTask().WaitAsync(PhaseTimeout);
            Ensure(echoed.SequenceEqual(probe),
                "a unary request on the same connection should succeed after peer stream terminal");
        }
        finally
        {
            interceptor.Release();
        }
    }

    [Test]
    [NotInParallel]
    public async Task TypedAttachedUnreadOneWayStreamShouldAbandonTypedBufferAndKeepConnectionUsable()
    {
        OneWayInboundDrainService.Reset();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
        });
        var service = harness.Client.Get<IOneWayInboundDrainService>();
        try
        {
            var send = service.IgnoreStreamAfterGateAsync(ManyPayloads(128, 32)).AsTask();

            await OneWayInboundDrainService.Entered.WaitAsync(PhaseTimeout);
            await Task.Delay(75);
            Ensure(!send.IsCompleted,
                "typed-attached unread stream should hold enough credit to stall a sender beyond the receive window");

            OneWayInboundDrainService.Release();
            await send.WaitAsync(PhaseTimeout);

            Ensure(OneWayInboundDrainService.EnumeratedItemCount == 0,
                "the service must return without enumerating the typed inbound stream");
            var probe = await service.PingAsync(41).AsTask().WaitAsync(PhaseTimeout);
            Ensure(probe == 42,
                "the connection should remain usable after typed-attached abandonment drains to peer terminal");
        }
        finally
        {
            OneWayInboundDrainService.Release();
        }
    }

    private static async IAsyncEnumerable<byte[]> ManyPayloads(
        int count,
        int size,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = Enumerable.Repeat((byte)0x2a, size).ToArray();
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return payload;
            await Task.Yield();
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ShortCircuitOneWayServerInterceptor : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            if (context.Method.Kind != RpcMethodKind.OneWay)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            // Deliberately do not invoke next. The server must abandon the inbound stream route
            // and continue returning receive credit until the peer sends StreamComplete.
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;

        public ISharpLinkServer Server { get; }
        public ISharpLinkClient Client { get; }

        private Harness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            Server = server;
            Client = client;
        }

        public static async Task<Harness> CreateAsync(Action<SharpLinkRuntimeOptions> runtimeConfigure)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .UseRuntime(runtimeConfigure);
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(
                () => server.RunAsync(cts.Token).AsTask(),
                CancellationToken.None);

            var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .UseRuntime(runtimeConfigure)
                .Build();
            await client.ConnectAsync(cts.Token);
            return new Harness(cts, serverTask, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _serverCts.CancelAsync();
            await Server.DisposeAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }
}

[RpcContract]
public interface IOneWayInboundDrainService : IService
{
    [Oneway]
    [NonCancellable]
    ValueTask IgnoreStreamAfterGateAsync(IAsyncEnumerable<byte[]> values);

    [Oneway]
    [NonCancellable]
    ValueTask IgnoreCorruptiblePayloadAndStreamAsync(
        byte[] payload,
        IAsyncEnumerable<byte[]> values);

    [NonCancellable]
    ValueTask<int> PingAsync(int value);
}

[RpcService]
public sealed class OneWayInboundDrainService : IOneWayInboundDrainService
{
    private static TaskCompletionSource s_entered = CreateCompletion();
    private static TaskCompletionSource s_release = CreateCompletion();
    private static int s_enumeratedItemCount;

    internal static Task Entered => Volatile.Read(ref s_entered).Task;
    internal static int EnumeratedItemCount => Volatile.Read(ref s_enumeratedItemCount);

    internal static void Reset()
    {
        Interlocked.Exchange(ref s_entered, CreateCompletion());
        Interlocked.Exchange(ref s_release, CreateCompletion());
        Volatile.Write(ref s_enumeratedItemCount, 0);
    }

    internal static void Release() => Volatile.Read(ref s_release).TrySetResult();

    public async ValueTask IgnoreStreamAfterGateAsync(IAsyncEnumerable<byte[]> values)
    {
        _ = values;
        Volatile.Read(ref s_entered).TrySetResult();
        await Volatile.Read(ref s_release).Task.ConfigureAwait(false);
        // Intentionally do not enumerate values. This is the already-typed-attached #304 case.
    }

    public async ValueTask IgnoreCorruptiblePayloadAndStreamAsync(
        byte[] payload,
        IAsyncEnumerable<byte[]> values)
    {
        _ = payload;
        _ = values;
        Volatile.Read(ref s_entered).TrySetResult();
        await Volatile.Read(ref s_release).Task.ConfigureAwait(false);
    }

    public ValueTask<int> PingAsync(int value) => ValueTask.FromResult(value + 1);

    private static TaskCompletionSource CreateCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
