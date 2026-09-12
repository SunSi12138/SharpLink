using System.Linq;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterDialConcurrencyTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task StartAsyncShouldBoundPhysicalDialsAcrossChildSupervisors()
    {
        var probe = new DialProbe();
        var transports = new[]
        {
            new BlockingSuccessfulTransportFactory(probe),
            new BlockingSuccessfulTransportFactory(probe),
            new BlockingSuccessfulTransportFactory(probe)
        };

        await using var client = CreateDynamicBuilder()
            .Configure(options => options.MaxConcurrentClusterConnects = 2)
            .AddCluster("alpha", child => child.UseTransport(transports[0]), slot => slot.AllowDynamicContracts = true)
            .AddCluster("beta", child => child.UseTransport(transports[1]), slot => slot.AllowDynamicContracts = true)
            .AddCluster("gamma", child => child.UseTransport(transports[2]), slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StartAsync();
        await WaitForConditionAsync(
            () => probe.Entries == 2,
            "exactly two physical dials should enter while both permits are occupied");

        Ensure(probe.Active == 2 && probe.MaxActive == 2,
            "MaxConcurrentClusterConnects=2 must bound the actual transport dial concurrency");
        Ensure(transports.Count(static transport => transport.ConnectCount != 0) == 2,
            "the third child supervisor must wait before invoking its transport factory");

        transports.First(static transport => transport.ConnectCount != 0).ReleaseConnect();
        await WaitForConditionAsync(
            () => probe.Entries == 3,
            "releasing one physical dial permit should admit the queued child transport attempt");
        Ensure(probe.MaxActive == 2,
            "admitting the queued child must not exceed the configured physical dial bound");

        foreach (var transport in transports)
            transport.ReleaseConnect();
        await client.WaitForReadyAsync().AsTask().WaitAsync(RaceCoordinationTimeout);
        Ensure(probe.Active == 0,
            "successful transport dials must release every shared permit");
    }

    [Test]
    public async Task RunningAddShouldPublishWhileItsPhysicalDialWaitsForPermit()
    {
        var probe = new DialProbe();
        var bootstrap = new BlockingSuccessfulTransportFactory(probe);
        var added = new BlockingSuccessfulTransportFactory(probe);

        await using var client = CreateDynamicBuilder()
            .Configure(options => options.MaxConcurrentClusterConnects = 1)
            .AddCluster(
                "bootstrap",
                child => child.UseTransport(bootstrap),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StartAsync();
        await bootstrap.Started.Task.WaitAsync(RaceCoordinationTimeout);

        var add = await AddClusterWithFixedDiscoveryAsync(
            client,
            "added",
            child => child.UseTransport(added),
            slot => slot.AllowDynamicContracts = true,
            manifests: [],
            routes: []).AsTask().WaitAsync(RaceCoordinationTimeout);

        Ensure(add.Succeeded,
            "a locally valid running Add must commit while remote readiness converges in the background");
        Ensure(added.ConnectCount == 0,
            "the published added child must still wait before invoking its physical transport dial");
        Ensure(client.GetClusterReadiness("added") == SharpLinkReadinessState.NotReady,
            "waiting for the shared dial permit must not make Add wait for remote Ready");
        Ensure(probe.MaxActive == 1,
            "running Add must share the same physical dial concurrency boundary");

        bootstrap.ReleaseConnect();
        await added.Started.Task.WaitAsync(RaceCoordinationTimeout);
        Ensure(probe.MaxActive == 1,
            "the added child may enter only after the previous physical dial releases its permit");

        added.ReleaseConnect();
        await client.WaitForReadyAsync("added").AsTask().WaitAsync(RaceCoordinationTimeout);
    }

    [Test]
    public async Task ReconnectShouldSharePhysicalDialPermitWithRuntimeAddedChild()
    {
        var probe = new DialProbe();
        var reconnectClock = new ManualTimeProvider();
        var recovering = new FailThenBlockingSuccessfulTransportFactory(probe);
        var blocker = new BlockingSuccessfulTransportFactory(probe);
        var reconnectPolicy = new SharpLinkReconnectPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            1d,
            1d,
            1d,
            TimeSpan.Zero);

        await using var client = CreateDynamicBuilder()
            .Configure(options => options.MaxConcurrentClusterConnects = 1)
            .AddCluster(
                "recovering",
                child => child
                    .UseTransport(recovering)
                    .UseTimeProvider(reconnectClock)
                    .UseReconnectPolicy(reconnectPolicy),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StartAsync();
        await recovering.FirstStarted.Task.WaitAsync(RaceCoordinationTimeout);

        var add = await AddClusterWithFixedDiscoveryAsync(
            client,
            "blocker",
            child => child.UseTransport(blocker),
            slot => slot.AllowDynamicContracts = true,
            manifests: [],
            routes: []).AsTask().WaitAsync(RaceCoordinationTimeout);
        Ensure(add.Succeeded && blocker.ConnectCount == 0,
            "the runtime-added blocker should queue behind the first physical dial");

        recovering.ReleaseFirstFailure();
        await blocker.Started.Task.WaitAsync(RaceCoordinationTimeout);
        await WaitForConditionAsync(
            () => reconnectClock.ActiveTimerCount != 0,
            "the failed child should arm its reconnect delay while another child owns the dial permit");

        reconnectClock.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();
        await Task.Yield();
        Ensure(recovering.ConnectCount == 1 && probe.MaxActive == 1,
            "a due reconnect must wait at the shared permit instead of bypassing the physical dial cap");

        blocker.ReleaseConnect();
        await recovering.SecondStarted.Task.WaitAsync(RaceCoordinationTimeout);
        Ensure(probe.MaxActive == 1,
            "the reconnect physical dial may enter only after the runtime-added child releases the permit");

        recovering.ReleaseSecondSuccess();
        await client.WaitForReadyAsync("recovering").AsTask().WaitAsync(RaceCoordinationTimeout);
    }

    [Test]
    public async Task ReplaceCandidateShouldWaitAtSamePhysicalDialBoundary()
    {
        var probe = new DialProbe();
        var blocker = new BlockingSuccessfulTransportFactory(probe);
        var replacement = new BlockingSuccessfulTransportFactory(probe);

        await using var client = CreateStaticBuilder()
            .Configure(options => options.MaxConcurrentClusterConnects = 1)
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();

        await client.StartAsync();
        await client.WaitForReadyAsync("orders").AsTask().WaitAsync(RaceCoordinationTimeout);

        var add = await AddClusterWithFixedDiscoveryAsync(
            client,
            "blocker",
            child => child.UseTransport(blocker),
            slot => slot.AllowDynamicContracts = true,
            manifests: [],
            routes: []).AsTask().WaitAsync(RaceCoordinationTimeout);
        Ensure(add.Succeeded,
            "the blocking runtime child must publish before replacement starts");
        await blocker.Started.Task.WaitAsync(RaceCoordinationTimeout);

        var replace = client.ReplaceClusterAsync(
            "orders",
            child => child.UseTransport(replacement),
            TimeSpan.Zero).AsTask();
        await Task.Yield();
        await Task.Yield();

        Ensure(replacement.ConnectCount == 0 && !replace.IsCompleted,
            "Replace must not bypass an occupied physical dial permit");
        Ensure(probe.MaxActive == 1,
            "replacement candidates must share MaxConcurrentClusterConnects with published children");

        blocker.ReleaseConnect();
        await replacement.Started.Task.WaitAsync(RaceCoordinationTimeout);
        Ensure(probe.MaxActive == 1,
            "replacement transport dial may start only after the previous physical attempt exits");

        replacement.ReleaseConnect();
        var result = await replace.WaitAsync(RaceCoordinationTimeout);
        Ensure(result.Succeeded && result.Published,
            "the replacement should commit after its permitted physical dial becomes available");
    }

    [Test]
    public async Task StopShouldCancelPermitOwnersAndWaitersWithoutDeadlock()
    {
        var probe = new DialProbe();
        var transports = new[]
        {
            new BlockingSuccessfulTransportFactory(probe),
            new BlockingSuccessfulTransportFactory(probe),
            new BlockingSuccessfulTransportFactory(probe)
        };
        var client = CreateDynamicBuilder()
            .Configure(options => options.MaxConcurrentClusterConnects = 1)
            .AddCluster("one", child => child.UseTransport(transports[0]), slot => slot.AllowDynamicContracts = true)
            .AddCluster("two", child => child.UseTransport(transports[1]), slot => slot.AllowDynamicContracts = true)
            .AddCluster("three", child => child.UseTransport(transports[2]), slot => slot.AllowDynamicContracts = true)
            .Build();

        try
        {
            await client.StartAsync();
            await WaitForConditionAsync(
                () => probe.Entries == 1,
                "one physical dial should own the sole permit before shutdown");

            await client.StopAsync().AsTask().WaitAsync(RaceCoordinationTimeout);
            Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Stopped,
                "shutdown must complete even when child supervisors own or await the shared dial permit");
            Ensure(probe.Active == 0 && probe.MaxActive == 1,
                "shutdown cancellation must release the active permit without admitting concurrent physical dials");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private sealed class DialProbe
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

    private sealed class BlockingSuccessfulTransportFactory(DialProbe probe) : IClientTransportFactory
    {
        private readonly TestClientTransportFactory _inner = new();
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            probe.Enter();
            Started.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                return await _inner.ConnectAsync(cancellationToken);
            }
            finally
            {
                probe.Exit();
            }
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        internal void ReleaseConnect() => _release.TrySetResult();
    }

    private sealed class FailThenBlockingSuccessfulTransportFactory(DialProbe probe) : IClientTransportFactory
    {
        private readonly TestClientTransportFactory _inner = new();
        private readonly TaskCompletionSource _firstRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        internal TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _connectCount);
            probe.Enter();
            try
            {
                if (attempt == 1)
                {
                    FirstStarted.TrySetResult();
                    await _firstRelease.Task.WaitAsync(cancellationToken);
                    throw new IOException("controlled initial physical dial failure");
                }

                SecondStarted.TrySetResult();
                await _secondRelease.Task.WaitAsync(cancellationToken);
                return await _inner.ConnectAsync(cancellationToken);
            }
            finally
            {
                probe.Exit();
            }
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        internal void ReleaseFirstFailure() => _firstRelease.TrySetResult();
        internal void ReleaseSecondSuccess() => _secondRelease.TrySetResult();
    }
}
