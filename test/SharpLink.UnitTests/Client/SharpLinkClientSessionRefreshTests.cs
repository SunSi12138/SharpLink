using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientSessionRefreshTests
{
    [Test]
    public async Task FixedRefreshShouldReplaceBeforeRetireAndDrainInflightCall()
    {
        var factory = new RefreshTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            factory,
            builder => builder.UseConnectionPool(options =>
            {
                options.MinConnections = 1;
                options.MaxConnections = 1;
            }));
        await client.ConnectAsync();
        Ensure(client.ReadyConnectionCount == 1 && factory.ConnectCount == 1,
            $"fixed test must start with one Ready physical connection; ready={client.ReadyConnectionCount}, connects={factory.ConnectCount}");
        var source = factory.GetConnection(0);
        var serverInstanceId = Guid.NewGuid();

        await InjectRefreshAsync(source, serverInstanceId, 2);
        await WaitForReplacementStartAsync(factory, client, "fixed");

        Ensure(client.ReadyConnectionCount == 1,
            $"the old fixed connection must remain Ready while its replacement is still connecting; ready={client.ReadyConnectionCount}");

        var inflight = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var inflightRequest = await source.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(2));

        await InjectRefreshAsync(source, serverInstanceId, 2);
        await InjectRefreshAsync(source, serverInstanceId, 1);
        await InjectRefreshAsync(source, serverInstanceId, 3);
        Ensure(factory.ConnectCount == 2,
            "stale, duplicate, and newer requests for one source must share the in-flight replacement");

        factory.ReleaseReplacement();
        var replacement = factory.GetConnection(1);
        _ = await replacement.WaitForSentPacket(ProtocolV2FrameType.Ping)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(client.ReadyConnectionCount == 1,
            $"replacement publication must atomically swap Ready eligibility instead of overshooting the pool; ready={client.ReadyConnectionCount}");

        var next = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var nextRequest = await replacement.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await replacement.InjectInt32ResponseAsync(unchecked((long)nextRequest.RequestId));
        Ensure(await next.WaitAsync(TimeSpan.FromSeconds(2)) == 0,
            "new work after replacement publication must use the replacement connection");

        await source.InjectInt32ResponseAsync(unchecked((long)inflightRequest.RequestId));
        Ensure(await inflight.WaitAsync(TimeSpan.FromSeconds(2)) == 0,
            "work accepted before the Ready swap must drain successfully on the old connection");

        Ensure(!await factory.WaitForThirdConnectAsync(TimeSpan.FromMilliseconds(250)),
            "coalesced refresh debt must not create another replacement after the first replacement converges");
    }

    [Test]
    public async Task StaticRefreshShouldPreserveSourceEndpointAffinityAndReadyCapacity()
    {
        var firstFactory = new RefreshTransportFactory();
        var secondFactory = new RefreshTransportFactory();
        var firstEndpoint = CreateEndpoint("first", 5001);
        var secondEndpoint = CreateEndpoint("second", 5002);
        await using var client = ClientBuilderTestHelper.BuildStatic(
        [
            new StaticEndpointConfiguration(firstEndpoint, firstFactory),
            new StaticEndpointConfiguration(secondEndpoint, secondFactory)
        ],
        ConfigureTwoEndpointCluster);
        await client.ConnectAsync();

        Ensure(client.ReadyConnectionCount == 2 && firstFactory.ConnectCount == 1 && secondFactory.ConnectCount == 1,
            $"static topology should begin with one Ready connection per source endpoint; ready={client.ReadyConnectionCount}, first={firstFactory.ConnectCount}, second={secondFactory.ConnectCount}");

        await InjectRefreshAsync(firstFactory.GetConnection(0), Guid.NewGuid(), 2);
        await WaitForReplacementStartAsync(firstFactory, client, "static");

        Ensure(client.ReadyConnectionCount == 2,
            $"a static source must stay Ready while its same-endpoint replacement is connecting; ready={client.ReadyConnectionCount}");
        Ensure(secondFactory.ConnectCount == 1,
            "static refresh must not migrate replacement work to another endpoint");

        firstFactory.ReleaseReplacement();
        _ = await firstFactory.GetConnection(1).WaitForSentPacket(ProtocolV2FrameType.Ping)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(client.ReadyConnectionCount == 2,
            $"static refresh must preserve the published Ready connection count after the swap; ready={client.ReadyConnectionCount}");
        Ensure(firstFactory.ConnectCount == 2 && secondFactory.ConnectCount == 1,
            "static replacement must retain exact source-endpoint transport ownership");
    }

    [Test]
    public async Task DynamicRefreshShouldPreserveCurrentEndpointGenerationAffinity()
    {
        var firstFactory = new RefreshTransportFactory();
        var secondFactory = new RefreshTransportFactory();
        var firstEndpoint = CreateEndpoint("first", 5001);
        var secondEndpoint = CreateEndpoint("second", 5002);
        var resolver = new DelegateSharpLinkEndpointResolver(
            _ => ValueTask.FromResult(new SharpLinkEndpointSnapshot(1, [firstEndpoint, secondEndpoint])),
            TimeSpan.FromHours(1));
        var factories = new Dictionary<string, RefreshTransportFactory>(StringComparer.Ordinal)
        {
            [firstEndpoint.Id] = firstFactory,
            [secondEndpoint.Id] = secondFactory
        };
        await using var client = ClientBuilderTestHelper.BuildDynamic(
            resolver,
            endpoint => factories[endpoint.Id],
            ConfigureTwoEndpointCluster);
        await client.ConnectAsync();

        Ensure(client.ReadyConnectionCount == 2 && firstFactory.ConnectCount == 1 && secondFactory.ConnectCount == 1,
            $"dynamic topology should begin with one Ready connection per current endpoint generation; ready={client.ReadyConnectionCount}, first={firstFactory.ConnectCount}, second={secondFactory.ConnectCount}");

        await InjectRefreshAsync(firstFactory.GetConnection(0), Guid.NewGuid(), 2);
        await WaitForReplacementStartAsync(firstFactory, client, "dynamic");

        Ensure(client.ReadyConnectionCount == 2,
            $"a dynamic source must stay Ready until its replacement for the same generation is Ready; ready={client.ReadyConnectionCount}");
        Ensure(secondFactory.ConnectCount == 1,
            "dynamic refresh must not move replacement work to a different endpoint generation");

        firstFactory.ReleaseReplacement();
        _ = await firstFactory.GetConnection(1).WaitForSentPacket(ProtocolV2FrameType.Ping)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(client.ReadyConnectionCount == 2,
            $"dynamic refresh must preserve Ready capacity across replacement publication; ready={client.ReadyConnectionCount}");
        Ensure(firstFactory.ConnectCount == 2 && secondFactory.ConnectCount == 1,
            "dynamic replacement must retain exact current endpoint-generation ownership");
    }

    private static void ConfigureTwoEndpointCluster(SharpClientBuilder builder)
        => builder.UseCluster(options =>
        {
            options.MinReadyEndpoints = 2;
            options.MaxConnections = 2;
            options.MaxConnectionsPerEndpoint = 1;
            options.MaxRetiringConnections = 2;
        });

    private static SharpLinkEndpoint CreateEndpoint(string id, int port)
        => new()
        {
            Id = id,
            Address = new SharpLinkTcpAddress("127.0.0.1", port)
        };

    private static async Task InjectRefreshAsync(
        TestTransportConnection connection,
        Guid serverInstanceId,
        ulong generation)
    {
        var writer = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteSessionRefreshRequested(
            writer,
            new ProtocolV2SessionRefreshRequested(serverInstanceId, generation));
        await connection.InjectFrameAsync(
            ProtocolV2FrameType.SessionRefreshRequested,
            ProtocolV2FrameFlags.None,
            0,
            writer.WrittenMemory);
    }

    private static async Task WaitForReplacementStartAsync(
        RefreshTransportFactory factory,
        SharpLinkClient client,
        string topology)
    {
        try
        {
            await factory.ReplacementStarted.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"{topology} refresh did not start a replacement; ready={client.ReadyConnectionCount}, connects={factory.ConnectCount}, state={client.State}",
                exception);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RefreshTransportFactory : IClientTransportFactory
    {
        private readonly Lock _gate = new();
        private readonly List<TestTransportConnection> _connections = [];
        private readonly TaskCompletionSource<bool> _replacementStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _thirdConnectStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseReplacement =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        internal int ConnectCount => Volatile.Read(ref _connectCount);
        internal Task ReplacementStarted => _replacementStarted.Task;

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref _connectCount);
            var connection = new TestTransportConnection();
            lock (_gate)
                _connections.Add(connection);

            try
            {
                if (ordinal >= 2)
                {
                    _replacementStarted.TrySetResult(true);
                    if (ordinal >= 3)
                        _thirdConnectStarted.TrySetResult(true);
                    await _releaseReplacement.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                await connection.InjectSuccessfulHandshakeAsync(
                    ProtocolV2Capabilities.SessionRefresh,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        internal TestTransportConnection GetConnection(int index)
        {
            lock (_gate)
                return _connections[index];
        }

        internal void ReleaseReplacement() => _releaseReplacement.TrySetResult(true);

        internal async Task<bool> WaitForThirdConnectAsync(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                await _thirdConnectStarted.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            TestTransportConnection[] connections;
            lock (_gate)
                connections = [.. _connections];
            for (var index = 0; index < connections.Length; index++)
                await connections[index].DisposeAsync().ConfigureAwait(false);
        }
    }
}
