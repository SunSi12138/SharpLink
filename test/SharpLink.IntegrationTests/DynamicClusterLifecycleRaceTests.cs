namespace SharpLink.IntegrationTests;

public sealed class DynamicClusterLifecycleRaceTests
{
    [Test]
    [NotInParallel]
    public async Task ConcurrentStopDuringReconnectShouldReleaseResolverAndFactoryExactlyOnce()
    {
        var resolver = new CountingResolver(
            new SharpLinkEndpointSnapshot(1, [Endpoint("failing", 1)]));
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

            var firstStop = client.StopAsync().AsTask();
            var secondStop = client.StopAsync().AsTask();
            await Task.WhenAll(firstStop, secondStop).WaitAsync(TimeSpan.FromSeconds(3));

            Ensure(((SharpLinkClient)client).State == SharpLinkConnectionState.Stopped,
                "concurrent stop calls must converge on the stopped state");
            Ensure(factory.ConnectCount == 2,
                "stop must cancel the active reconnect without scheduling another dial");
            Ensure(resolver.DisposeCount == 1,
                "concurrent stop calls must dispose the resolver exactly once");
            Ensure(factory.DisposeCount == 1,
                "concurrent stop calls must dispose the endpoint factory exactly once");
        }
        finally
        {
            await client.DisposeAsync();
        }

        Ensure(resolver.DisposeCount == 1,
            "dispose after concurrent stop must not dispose the resolver again");
        Ensure(factory.DisposeCount == 1,
            "dispose after concurrent stop must not dispose the endpoint factory again");
    }

    private static SharpLinkEndpoint Endpoint(string id, int port) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
    };

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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class CountingResolver(SharpLinkEndpointSnapshot initial) : ISharpLinkEndpointResolver
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(initial);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
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
}
