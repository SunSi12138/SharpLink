using System.Threading.Channels;

namespace SharpLink.IntegrationTests;

// Characterization boundary for DynamicClusterRuntime before the extractions tracked by #343.
//
// Invariants intentionally pinned here:
// - A newer topology publication makes a retired endpoint generation unavailable to new selection,
//   even when a caller still holds an older selection snapshot. Add/remove and attributes-only
//   publication are covered by DynamicEndpointIntegrationTests.DynamicResolverShouldAddRemoveReplaceAndUpdateAttributesWithoutReconnecting.
// - Retiring removes a connection from the ready/selection snapshot immediately, while an already
//   accepted call may keep that connection and its generation-owned transport factory alive until drain completes.
// - Stop owns resolver/reconnect shutdown: reconnect work is cancelled and awaited before each resolver/factory
//   is released exactly once, and no reconnect may be scheduled after stop completes.
public sealed class DynamicClusterRuntimeCharacterizationTests
{
    [Test]
    [NotInParallel]
    public async Task TopologyReplacementShouldRejectAStaleSelectionAndPublishTheNewGeneration()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var replacement = await TcpServerScope.StartAsync("replacement");
        var resolver = new ControllableResolver(
            new SharpLinkEndpointSnapshot(1, [Endpoint("node", first.Port, "blue")]));
        using var selector = new PauseFirstSelectionSelector();
        var sockets = SharpLinkTransportFactories.Sockets();
        TrackingTransportFactory? firstFactory = null;
        TrackingTransportFactory? replacementFactory = null;
        var factoryCreates = 0;
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseEndpointResolver(
                resolver,
                endpoint =>
                {
                    var factory = new TrackingTransportFactory(sockets(endpoint));
                    if (Interlocked.Increment(ref factoryCreates) == 1)
                        firstFactory = factory;
                    else
                        replacementFactory = factory;
                    return factory;
                })
            .UseEndpointSelector(selector)
            .Build();

        try
        {
            await client.ConnectAsync();
            var service = client.Get<IConnectionBehaviorService>();
            var staleCall = Task.Run(async () => await service.GetEndpointIdAsync());
            await selector.Entered.WaitAsync(TimeSpan.FromSeconds(2));

            resolver.Publish(new SharpLinkEndpointSnapshot(2, [Endpoint("node", replacement.Port, "green")]));
            await WaitUntilAsync(
                () => replacementFactory is { ConnectCount: > 0 } &&
                      firstFactory is { DisposeCount: 1 } &&
                      ((SharpLinkClient)client).ReadyConnectionCount == 1,
                TimeSpan.FromSeconds(4));

            Ensure(await service.GetEndpointIdAsync() == "replacement",
                "a call selecting after replacement publication must use the new generation");

            selector.Release();
            var staleFailure = await CaptureSharpLinkException(staleCall.WaitAsync(TimeSpan.FromSeconds(3)));
            Ensure(staleFailure.Code == SharpLinkErrorCode.Unavailable,
                "a caller holding the retired selection snapshot must not reuse its old connection");
        }
        finally
        {
            selector.Release();
            await client.DisposeAsync();
        }

        Ensure(resolver.DisposeCount == 1, "dynamic stop must dispose the resolver exactly once");
        Ensure(firstFactory is { DisposeCount: 1 }, "retired generation factory must be released exactly once");
        Ensure(replacementFactory is { DisposeCount: 1 }, "current generation factory must be released at stop");
    }

    [Test]
    [NotInParallel]
    public async Task RetiringGenerationShouldDrainAcceptedStreamBeforeReleasingItsFactory()
    {
        await using var first = await TcpServerScope.StartAsync("first");
        await using var replacement = await TcpServerScope.StartAsync("replacement");
        var resolver = new ControllableResolver(
            new SharpLinkEndpointSnapshot(1, [Endpoint("node", first.Port, "blue")]));
        var sockets = SharpLinkTransportFactories.Sockets();
        TrackingTransportFactory? firstFactory = null;
        TrackingTransportFactory? replacementFactory = null;
        var factoryCreates = 0;
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .UseEndpointResolver(
                resolver,
                endpoint =>
                {
                    var factory = new TrackingTransportFactory(sockets(endpoint));
                    if (Interlocked.Increment(ref factoryCreates) == 1)
                        firstFactory = factory;
                    else
                        replacementFactory = factory;
                    return factory;
                })
            .Build();

        try
        {
            await client.ConnectAsync();
            var service = client.Get<IConnectionBehaviorService>();
            await using var stream = service.SlowRangeAsync(3, 80, CancellationToken.None).GetAsyncEnumerator();
            Ensure(await stream.MoveNextAsync() && stream.Current == 0, "first item must be accepted on the original generation");

            resolver.Publish(new SharpLinkEndpointSnapshot(2, [Endpoint("node", replacement.Port, "green")]));
            await WaitUntilAsync(
                () => replacementFactory is { ConnectCount: > 0 } &&
                      ((SharpLinkClient)client).ReadyConnectionCount == 1,
                TimeSpan.FromSeconds(4));

            Ensure(firstFactory is { DisposeCount: 0 },
                "the retiring generation factory must stay owned while its accepted stream is active");
            Ensure(await service.GetEndpointIdAsync() == "replacement",
                "new calls must leave the retiring generation immediately");
            Ensure(await stream.MoveNextAsync() && stream.Current == 1,
                "retiring must not abort an already accepted stream");
            Ensure(await stream.MoveNextAsync() && stream.Current == 2,
                "the accepted stream must remain bound through its final item");
            Ensure(!await stream.MoveNextAsync(), "the accepted stream must complete normally");

            await WaitUntilAsync(() => firstFactory is { DisposeCount: 1 }, TimeSpan.FromSeconds(3));
            Ensure(replacementFactory is { DisposeCount: 0 },
                "the current generation factory remains owned until client stop");
        }
        finally
        {
            await client.DisposeAsync();
        }

        Ensure(firstFactory is { DisposeCount: 1 }, "retired generation factory must not be double-disposed");
        Ensure(replacementFactory is { DisposeCount: 1 }, "current generation factory must be released at stop");
    }

    [Test]
    [NotInParallel]
    public async Task StopShouldCancelReconnectAndReleaseResolverAndFactoryExactlyOnce()
    {
        var resolver = new ControllableResolver(
            new SharpLinkEndpointSnapshot(1, [Endpoint("failing", 1, "red")]));
        var factory = new FailThenBlockReconnectFactory();
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseEndpointResolver(resolver, _ => factory)
            .Build();

        try
        {
            var initialFailure = await CaptureSharpLinkException(client.ConnectAsync().AsTask());
            Ensure(initialFailure.Code == SharpLinkErrorCode.Unavailable,
                "the initial dynamic dial failure must surface as unavailable");
            await factory.ReconnectEntered.WaitAsync(TimeSpan.FromSeconds(3));

            await client.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

            Ensure(((SharpLinkClient)client).State == SharpLinkConnectionState.Stopped,
                "stop must complete only after the reconnect worker exits");
            Ensure(factory.ConnectCount == 2,
                "stop must prevent a cancelled reconnect worker from scheduling another dial");
            Ensure(resolver.DisposeCount == 1, "stop owns resolver disposal exactly once");
            Ensure(factory.DisposeCount == 1, "stop owns the current generation factory exactly once");
        }
        finally
        {
            await client.DisposeAsync();
        }

        Ensure(resolver.DisposeCount == 1, "dispose after stop must not dispose the resolver again");
        Ensure(factory.DisposeCount == 1, "dispose after stop must not dispose the factory again");
    }

    private static SharpLinkEndpoint Endpoint(string id, int port, string zone) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port),
        Attributes = new Dictionary<string, string> { ["zone"] = zone }
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition() && Stopwatch.GetTimestamp() < deadline)
            await Task.Delay(20);
        if (!condition())
            throw new TimeoutException("Dynamic cluster did not reach the expected characterization state.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static async Task<SharpLinkException> CaptureSharpLinkException(Task task)
    {
        try
        {
            await task;
            throw new Exception("expected SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private sealed class ControllableResolver(SharpLinkEndpointSnapshot initial) : ISharpLinkEndpointResolver
    {
        private readonly Channel<SharpLinkEndpointSnapshot> _snapshots = Channel.CreateUnbounded<SharpLinkEndpointSnapshot>();
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(initial);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(cancellationToken))
                yield return snapshot;
        }

        public void Publish(SharpLinkEndpointSnapshot snapshot)
            => _snapshots.Writer.TryWrite(snapshot);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
                _snapshots.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingTransportFactory(IClientTransportFactory inner) : IClientTransportFactory
    {
        private int _connectCount;
        private int _disposeCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            return await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class FailThenBlockReconnectFactory : IClientTransportFactory
    {
        private readonly TaskCompletionSource _reconnectEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;
        private int _disposeCount;

        public Task ReconnectEntered => _reconnectEntered.Task;
        public int ConnectCount => Volatile.Read(ref _connectCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _connectCount) == 1)
                throw new InvalidOperationException("test initial dial failure");

            _reconnectEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PauseFirstSelectionSelector : ISharpLinkEndpointSelector, IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _pauseNext = 1;

        public Task Entered => _entered.Task;

        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            if (Interlocked.Exchange(ref _pauseNext, 0) == 1)
            {
                _entered.TrySetResult();
                if (!_release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The paused endpoint selection was not released.");
            }
            return 0;
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class TcpServerScope : IAsyncDisposable
    {
        private readonly ISharpLinkServer _server;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _runTask;
        private int _stopped;

        private TcpServerScope(ISharpLinkServer server, int port)
        {
            _server = server;
            Port = port;
            _runTask = Task.Run(() => _server.RunAsync(_cancellation.Token).AsTask(), CancellationToken.None);
        }

        public int Port { get; }

        public async ValueTask StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            await _server.StopAsync(TimeSpan.Zero);
            await _cancellation.CancelAsync();
            await Task.WhenAny(_runTask, Task.Delay(1000));
        }

        public static Task<TcpServerScope> StartAsync(string endpointId)
        {
            var builder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            builder.ReplaceService<IConnectionBehaviorService>(new ConnectionBehaviorService { EndpointId = endpointId });
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            return Task.FromResult(new TcpServerScope(builder.Build(), port));
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await _server.DisposeAsync();
            _cancellation.Dispose();
        }
    }
}
