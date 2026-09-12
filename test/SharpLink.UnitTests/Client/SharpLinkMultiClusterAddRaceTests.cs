using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterAddRaceTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task ConcurrentSameKeyAddsShouldRemainSerializedBeforePublication()
    {
        var winnerTransport = new SynchronouslyBlockingTransportFactory();
        var loserTransport = new ControlledMutationTransportFactory();
        await using var client = CreateDynamicBuilder()
            .AddCluster(
                "bootstrap",
                child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.StartAsync();
        await client.WaitForReadyAsync("bootstrap").AsTask().WaitAsync(RaceCoordinationTimeout);

        var winner = Task.Run(async () =>
            await AddClusterWithFixedDiscoveryAsync(
                client,
                "candidate",
                child => child.UseTransport(winnerTransport),
                slot => slot.AllowDynamicContracts = true));
        await winnerTransport.ConnectStarted.Task.WaitAsync(RaceCoordinationTimeout);

        var loser = AddClusterWithFixedDiscoveryAsync(
            client,
            "candidate",
            child => child.UseTransport(loserTransport),
            slot => slot.AllowDynamicContracts = true).AsTask();
        await Task.Delay(50);
        Ensure(!loser.IsCompleted,
            "a second same-key Add must remain serialized while the first candidate Start is in progress");

        winnerTransport.ReleaseConnect();
        await winner.WaitAsync(RaceCoordinationTimeout);
        var loserResult = await loser.WaitAsync(RaceCoordinationTimeout);

        Ensure(loserResult is
        {
            Succeeded: false,
            FailureCode: SharpLinkClusterMutationFailureCode.AlreadyExists
        },
            "the serialized loser must observe the first Add publication as a structured duplicate rejection");
        Ensure(winnerTransport.ConnectCount == 1 && winnerTransport.DisposeCount == 0,
            "the winning candidate must remain coordinator-owned after publication");
        Ensure(loserTransport.ConnectCount == 0 && loserTransport.DisposeCount == 1,
            "the losing candidate must not start connectivity and must release its resources");
    }

    [Test]
    public async Task ParentStopAfterCandidateStartShouldRollbackBeforePublication()
    {
        var candidateTransport = new StopParentOnConnectTransportFactory();
        await using var client = CreateDynamicBuilder()
            .AddCluster(
                "bootstrap",
                child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.StartAsync();
        await client.WaitForReadyAsync("bootstrap").AsTask().WaitAsync(RaceCoordinationTimeout);

        Task? stop = null;
        candidateTransport.OnConnect = () => stop = client.StopAsync().AsTask();

        var addResult = await AddClusterWithFixedDiscoveryAsync(
            client,
            "candidate",
            child => child.UseTransport(candidateTransport),
            slot => slot.AllowDynamicContracts = true);

        Ensure(candidateTransport.ConnectStarted.Task.IsCompleted,
            "the child runtime must start before the parent Stop wins the publication race");
        Ensure(addResult is
        {
            Succeeded: false,
            FailureCode: SharpLinkClusterMutationFailureCode.LifecycleClosed
        },
            "parent Stop must make the post-Start publication revalidation return LifecycleClosed");
        Ensure(candidateTransport.DisposeCount == 1,
            "the started but unpublished candidate must be stopped and disposed exactly once");
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState("candidate");
            return Task.CompletedTask;
        });

        await (stop ?? throw new Exception("parent Stop was not started"))
            .WaitAsync(RaceCoordinationTimeout);
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Stopped,
            "the parent Stop operation must complete after the Add releases the mutation gate");
    }

    private sealed class SynchronouslyBlockingTransportFactory : IClientTransportFactory
    {
        private readonly TestClientTransportFactory _inner = new();
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;
        private int _disposeCount;

        internal TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ConnectCount => Volatile.Read(ref _connectCount);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            ConnectStarted.TrySetResult();
            _release.Task.Wait(cancellationToken);
            return _inner.ConnectAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            _release.TrySetResult();
            await _inner.DisposeAsync();
        }

        internal void ReleaseConnect() => _release.TrySetResult();
    }

    private sealed class StopParentOnConnectTransportFactory : IClientTransportFactory
    {
        private int _disposeCount;

        internal Action? OnConnect { get; set; }
        internal TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult();
            OnConnect?.Invoke();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("cancelled candidate connect should not continue");
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
