using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkMultiClusterSessionRefreshDialTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task SimultaneousChildRefreshesShouldSharePhysicalDialPermit()
    {
        var probe = new RefreshDialProbe();
        var first = new RefreshDialTransportFactory(probe);
        var second = new RefreshDialTransportFactory(probe);

        await using var client = CreateDynamicBuilder()
            .Configure(options => options.MaxConcurrentClusterConnects = 1)
            .AddCluster("alpha", child => child.UseTransport(first), slot => slot.AllowDynamicContracts = true)
            .AddCluster("beta", child => child.UseTransport(second), slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StartAsync();
        await client.WaitForReadyAsync("alpha").AsTask().WaitAsync(RaceCoordinationTimeout);
        await client.WaitForReadyAsync("beta").AsTask().WaitAsync(RaceCoordinationTimeout);

        var serverInstance = Guid.NewGuid();
        await InjectRefreshAsync(first.GetConnection(0), serverInstance, 2);
        await InjectRefreshAsync(second.GetConnection(0), serverInstance, 2);

        await WaitForConditionAsync(
            () => probe.Entries == 1,
            "exactly one refresh replacement dial should enter the transport while the shared permit is occupied");
        Ensure(probe.Active == 1 && probe.MaxActive == 1,
            "MaxConcurrentClusterConnects=1 must include session-refresh replacement dials");
        Ensure(first.ConnectCount + second.ConnectCount == 3,
            "only one of two child refreshes may invoke its replacement transport while the permit is held");

        if (first.ReplacementStarted.IsCompleted)
            first.ReleaseReplacement();
        else
            second.ReleaseReplacement();

        await WaitForConditionAsync(
            () => probe.Entries == 2,
            "the queued child refresh should enter only after the first replacement dial releases the permit");
        Ensure(probe.MaxActive == 1,
            "simultaneous child refreshes must never overlap their physical transport dials");

        first.ReleaseReplacement();
        second.ReleaseReplacement();
        await WaitForConditionAsync(
            () => probe.Active == 0,
            "both refresh replacement dials should release the shared permit after transport creation");
    }

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

    private sealed class RefreshDialProbe
    {
        private int _active;
        private int _maxActive;
        private int _entries;

        internal int Active => Volatile.Read(ref _active);
        internal int MaxActive => Volatile.Read(ref _maxActive);
        internal int Entries => Volatile.Read(ref _entries);

        internal void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _entries);
            while (true)
            {
                var observed = Volatile.Read(ref _maxActive);
                if (active <= observed)
                    return;
                if (Interlocked.CompareExchange(ref _maxActive, active, observed) == observed)
                    return;
            }
        }

        internal void Exit() => Interlocked.Decrement(ref _active);
    }

    private sealed class RefreshDialTransportFactory(RefreshDialProbe probe) : IClientTransportFactory
    {
        private readonly Lock _gate = new();
        private readonly List<TestTransportConnection> _connections = [];
        private readonly TaskCompletionSource _replacementStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseReplacement =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        internal int ConnectCount => Volatile.Read(ref _connectCount);
        internal Task ReplacementStarted => _replacementStarted.Task;

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref _connectCount);
            var replacement = ordinal >= 2;
            if (replacement)
            {
                probe.Enter();
                _replacementStarted.TrySetResult();
            }

            var connection = new TestTransportConnection();
            lock (_gate)
                _connections.Add(connection);
            try
            {
                if (replacement)
                    await _releaseReplacement.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            finally
            {
                if (replacement)
                    probe.Exit();
            }
        }

        internal TestTransportConnection GetConnection(int index)
        {
            lock (_gate)
                return _connections[index];
        }

        internal void ReleaseReplacement() => _releaseReplacement.TrySetResult();

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
