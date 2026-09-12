using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientSessionRefreshPreCutFailureTests
{
    [Test]
    public async Task FixedReplacementFailureBeforeCutShouldKeepSourceSelectableAndRetry()
    {
        var factory = new PreCutFailureTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            factory,
            builder => builder.UseConnectionPool(options =>
            {
                options.MinConnections = 1;
                options.MaxConnections = 1;
            }));
        await client.ConnectAsync();

        await AssertPreCutReplacementFailureKeepsSourceAsync(client, factory);
    }

    [Test]
    public async Task StaticReplacementFailureBeforeCutShouldKeepSourceSelectableAndRetry()
    {
        var activeFactory = new PreCutFailureTransportFactory();
        var spareFactory = new PreCutFailureTransportFactory();
        await using var client = ClientBuilderTestHelper.BuildStatic(
        [
            new StaticEndpointConfiguration(CreateEndpoint("precut-static", 6501), activeFactory),
            new StaticEndpointConfiguration(CreateEndpoint("precut-static-spare", 6502), spareFactory)
        ],
        builder => builder.UseCluster(options =>
        {
            options.MinReadyEndpoints = 1;
            options.MaxConnections = 1;
            options.MaxConnectionsPerEndpoint = 1;
            options.MaxRetiringConnections = 1;
        }));
        await client.ConnectAsync();
        Ensure(activeFactory.ConnectCount == 1,
            "the static pre-cut failure setup should initially own the active endpoint");

        await AssertPreCutReplacementFailureKeepsSourceAsync(client, activeFactory);
        Ensure(spareFactory.ConnectCount == 0,
            "a failed pre-cut replacement must retain refresh debt on the source endpoint rather than migrate topology");
    }

    [Test]
    public async Task DynamicReplacementFailureBeforeCutShouldKeepSourceSelectableAndRetry()
    {
        var factory = new PreCutFailureTransportFactory();
        var endpoint = CreateEndpoint("precut-dynamic", 6601);
        var resolver = new DelegateSharpLinkEndpointResolver(
            _ => ValueTask.FromResult(new SharpLinkEndpointSnapshot(1, [endpoint])),
            TimeSpan.FromHours(1));
        await using var client = ClientBuilderTestHelper.BuildDynamic(
            resolver,
            _ => factory,
            builder => builder.UseCluster(options =>
            {
                options.MinReadyEndpoints = 1;
                options.MaxConnections = 1;
                options.MaxConnectionsPerEndpoint = 1;
                options.MaxRetiringConnections = 1;
            }));
        await client.ConnectAsync();

        await AssertPreCutReplacementFailureKeepsSourceAsync(client, factory);
    }

    private static async Task AssertPreCutReplacementFailureKeepsSourceAsync(
        SharpLinkClient client,
        PreCutFailureTransportFactory factory)
    {
        var sourceTransport = factory.GetConnection(0);
        var commitEntered = new TaskCompletionSource<ClientConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCommit = new ManualResetEventSlim(false);
        client._beforeSessionRefreshEligibilityCommitTestHook = replacement =>
        {
            commitEntered.TrySetResult(replacement);
            if (!releaseCommit.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("pre-cut replacement commit hook was not released");
        };

        try
        {
            await InjectRefreshAsync(sourceTransport, Guid.NewGuid(), 2);
            await factory.FirstReplacementStarted.WaitAsync(TimeSpan.FromSeconds(3));
            factory.ReleaseFirstReplacement();

            var replacement = await commitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var replacementTransport = factory.GetConnection(1);
            await replacementTransport.DisposeAsync();

            await WaitForConditionAsync(
                () => replacement.HasObservedFatalFailureForAdmission,
                "replacement receive/disconnect handling should publish failure observation before the eligibility cut");
            Ensure(client.ReadyConnectionCount == 1,
                "while the failed replacement is frozen before the cut, the healthy source must remain the sole Ready connection");

            releaseCommit.Set();
            await factory.RetryStarted.WaitAsync(TimeSpan.FromSeconds(3));

            Ensure(client.ReadyConnectionCount == 1,
                "rolling back a failed pre-cut replacement must not publish zero-ready or retire the healthy source");

            var unary = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
            var request = await sourceTransport.WaitForSentPacket(ProtocolV2FrameType.Request)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await sourceTransport.InjectInt32ResponseAsync(unchecked((long)request.RequestId));
            Ensure(await unary.WaitAsync(TimeSpan.FromSeconds(2)) == 0,
                "the original source must remain selectable after a replacement fails before the blue-green cut");
        }
        finally
        {
            releaseCommit.Set();
            factory.ReleaseRetry();
            client._beforeSessionRefreshEligibilityCommitTestHook = null;
        }
    }

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

    private static async Task WaitForConditionAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException(message);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class PreCutFailureTransportFactory : IClientTransportFactory
    {
        private readonly Lock _gate = new();
        private readonly List<TestTransportConnection> _connections = [];
        private readonly TaskCompletionSource _firstReplacementStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstReplacement =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _retryStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRetry =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        internal int ConnectCount => Volatile.Read(ref _connectCount);
        internal Task FirstReplacementStarted => _firstReplacementStarted.Task;
        internal Task RetryStarted => _retryStarted.Task;

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref _connectCount);
            var connection = new TestTransportConnection();
            lock (_gate)
                _connections.Add(connection);

            try
            {
                if (ordinal == 2)
                {
                    _firstReplacementStarted.TrySetResult();
                    await _releaseFirstReplacement.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (ordinal == 3)
                {
                    _retryStarted.TrySetResult();
                    await _releaseRetry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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

        internal void ReleaseFirstReplacement() => _releaseFirstReplacement.TrySetResult();
        internal void ReleaseRetry() => _releaseRetry.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            ReleaseFirstReplacement();
            ReleaseRetry();
            TestTransportConnection[] connections;
            lock (_gate)
                connections = [.. _connections];
            for (var index = 0; index < connections.Length; index++)
                await connections[index].DisposeAsync().ConfigureAwait(false);
        }
    }
}
