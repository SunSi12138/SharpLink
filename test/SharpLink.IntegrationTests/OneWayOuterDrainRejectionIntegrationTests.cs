using System.Collections.Concurrent;
using System.Reflection;

namespace SharpLink.IntegrationTests;

public class OneWayOuterDrainRejectionIntegrationTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [NotInParallel]
    public async Task OneWayClientStreamRejectedAfterConnectionStartsDrainingShouldReturnCredit()
    {
        OneWayInboundDrainService.Reset();
        await using var harness = await Harness.CreateAsync(options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 128;
        });
        var interceptor = new HoldUnaryServerInterceptor();
        harness.Server.ReplaceInterceptors([interceptor]);
        var service = harness.Client.Get<IOneWayInboundDrainService>();

        try
        {
            var activeUnary = service.PingAsync(51).AsTask();
            await interceptor.Entered.WaitAsync(PhaseTimeout);

            harness.MarkConnectionDraining();

            var rejectedSend = service.IgnoreStreamAfterGateAsync(
                ManyPayloads(128, 32)).AsTask();
            await rejectedSend.WaitAsync(PhaseTimeout);

            Ensure(!OneWayInboundDrainService.Entered.IsCompleted,
                "a OneWay request rejected by the outer connection-drain gate must not invoke the service");

            interceptor.Release();
            var activeResult = await activeUnary.WaitAsync(PhaseTimeout);
            Ensure(activeResult == 52,
                "the already-accepted unary call should complete after the rejected OneWay stream returns its receive credit");
        }
        finally
        {
            interceptor.Release();
        }
    }

    private static async IAsyncEnumerable<byte[]> ManyPayloads(
        int count,
        int size,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = Enumerable.Repeat((byte)0x39, size).ToArray();
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

    private sealed class HoldUnaryServerInterceptor : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            if (context.Method.Kind == RpcMethodKind.OneWay)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            await next(context).ConfigureAwait(false);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private static readonly FieldInfo ConnectionsField = typeof(SharpLinkServer).GetField(
            "_connections", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("cannot find server connection registry");

        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;

        internal ISharpLinkServer Server { get; }
        internal ISharpLinkClient Client { get; }

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

        internal static async Task<Harness> CreateAsync(Action<SharpLinkRuntimeOptions> runtimeConfigure)
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
                .DisableRequestTimeout()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .UseRuntime(runtimeConfigure)
                .Build();
            await client.ConnectAsync(cts.Token);
            return new Harness(cts, serverTask, server, client);
        }

        internal void MarkConnectionDraining()
        {
            var server = (SharpLinkServer)Server;
            var connections = (ConcurrentDictionary<string, ServerConnectionState>)(
                ConnectionsField.GetValue(server)
                ?? throw new InvalidOperationException("server connection registry is unavailable"));
            var connection = connections.Values.Single();
            Ensure(connection.ActiveCalls > 0,
                "the connection should retain an already-accepted call while entering drain");
            connection.MarkDraining();
            Ensure(connection.LifecycleState == ServerConnectionLifecycleState.Draining,
                "the server connection should enter draining state without closing the session");
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
