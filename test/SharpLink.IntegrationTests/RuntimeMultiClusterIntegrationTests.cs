using System.Runtime.CompilerServices;
using System.Threading.Channels;

[assembly: SharpLinkClusterContractAssembly(
    "runtime",
    typeof(SharpLink.IntegrationTests.IConnectionBehaviorService))]

namespace SharpLink.IntegrationTests;

public sealed class RuntimeMultiClusterIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task RuntimeTcpSlotShouldAddReplaceAndRemoveWithoutRebindingOldProxy()
    {
        await using var first = await ServerScope.StartAsync("first");
        await using var second = await ServerScope.StartAsync("second");
        await using var client = SharpLinkMultiClusterClientBuilder.Create().DisableRequestTimeout()
            .AddCluster(
                "bootstrap",
                child => child.UseTcp(IPAddress.Loopback.ToString(), first.Port),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.ConnectAsync();
        await client.AddClusterAsync(
            "runtime",
            child => child.UseTcp(IPAddress.Loopback.ToString(), first.Port));

        var oldProxy = client.Get<IConnectionBehaviorService>();
        Ensure(await oldProxy.GetEndpointIdAsync() == "first",
            "a ready coordinator must connect the candidate before publishing its route");

        await client.ReplaceClusterAsync(
            "runtime",
            child => child.UseTcp(IPAddress.Loopback.ToString(), second.Port),
            TimeSpan.FromSeconds(2));

        var newProxy = client.Get<IConnectionBehaviorService>();
        Ensure(await newProxy.GetEndpointIdAsync() == "second",
            "a proxy created after replacement must bind to the new child");
        var oldFailure = await CaptureExceptionAsync(oldProxy.GetEndpointIdAsync().AsTask());
        Ensure(oldFailure is SharpLinkException or InvalidOperationException,
            "a proxy created before replacement must remain bound to the retired child");

        var removal = await client.RemoveClusterAsync("runtime", TimeSpan.FromSeconds(2));
        Ensure(removal is { Succeeded: true, ReferencesReleased: true, ForcedStop: false },
            "a completed child stop must be reported as a graceful removal");
        var missingRoute = CaptureException(() => client.Get<IConnectionBehaviorService>());
        Ensure(missingRoute is InvalidOperationException,
            "new proxy creation must fail immediately after the slot is removed");
    }

    [Test]
    [NotInParallel]
    public async Task RuntimeDynamicResolverShouldUpdateEndpointsWithoutReplacingTheSlot()
    {
        await using var first = await ServerScope.StartAsync("resolver-first");
        await using var second = await ServerScope.StartAsync("resolver-second");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(
            1,
            [Endpoint("resolver-first", first.Port)]));
        await using var client = SharpLinkMultiClusterClientBuilder.Create().DisableRequestTimeout()
            .AddCluster(
                "bootstrap",
                child => child.UseTcp(IPAddress.Loopback.ToString(), first.Port),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync();

        await client.AddClusterAsync(
            "runtime",
            child => child
                .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
                .UseCluster(options =>
                {
                    options.MinReadyEndpoints = 1;
                    options.MaxConnections = 1;
                    options.MaxConnectionsPerEndpoint = 1;
                }));
        var proxy = client.Get<IConnectionBehaviorService>();
        Ensure(await proxy.GetEndpointIdAsync() == "resolver-first",
            "runtime resolver slot must use its initial endpoint");

        resolver.Publish(new SharpLinkEndpointSnapshot(
            2,
            [Endpoint("resolver-second", second.Port)]));
        await WaitUntilAsync(
            async () => await proxy.GetEndpointIdAsync() == "resolver-second",
            TimeSpan.FromSeconds(4));

        Ensure(await proxy.GetEndpointIdAsync() == "resolver-second",
            "resolver publication must update the existing slot without ReplaceClusterAsync");
        var removal = await client.RemoveClusterAsync("runtime", TimeSpan.FromSeconds(2));
        Ensure(removal.ReferencesReleased && resolver.DisposeCount == 1,
            "removed dynamic-resolver slot must release its resolver exactly once");
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static SharpLinkEndpoint Endpoint(string id, int port)
        => new()
        {
            Id = id,
            Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
        };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                    return;
            }
            catch (SharpLinkException)
            {
            }
            await Task.Delay(20);
        }

        Ensure(await condition(), "condition did not become true before the timeout");
    }

    private sealed class ControllableResolver(SharpLinkEndpointSnapshot initial) : ISharpLinkEndpointResolver
    {
        private readonly Channel<SharpLinkEndpointSnapshot> _snapshots =
            Channel.CreateUnbounded<SharpLinkEndpointSnapshot>();
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(initial);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(cancellationToken))
                yield return snapshot;
        }

        internal void Publish(SharpLinkEndpointSnapshot snapshot)
            => _snapshots.Writer.TryWrite(snapshot);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
                _snapshots.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ServerScope : IAsyncDisposable
    {
        private readonly ISharpLinkServer _server;
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _runTask;

        private ServerScope(
            int port,
            ISharpLinkServer server,
            CancellationTokenSource shutdown,
            Task runTask)
        {
            Port = port;
            _server = server;
            _shutdown = shutdown;
            _runTask = runTask;
        }

        internal int Port { get; }

        internal static Task<ServerScope> StartAsync(string endpointId)
        {
            var builder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .ReplaceService<IConnectionBehaviorService>(
                    new ConnectionBehaviorService { EndpointId = endpointId });
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            var server = builder.Build();
            var shutdown = new CancellationTokenSource();
            var runTask = server.RunAsync(shutdown.Token).AsTask();
            return Task.FromResult(new ServerScope(port, server, shutdown, runTask));
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            await _server.DisposeAsync();
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                _shutdown.Dispose();
            }
        }
    }
}
