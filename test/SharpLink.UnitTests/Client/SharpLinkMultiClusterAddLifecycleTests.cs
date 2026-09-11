using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterAddLifecycleTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task ImmediateFirstDialFailureShouldStillCommitRunningAdd()
    {
        var candidateTransport = new ControlledMutationTransportFactory(
            connectFailure: new InvalidOperationException("controlled added-cluster dial failure"));
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.StartAsync();
        await client.WaitForReadyAsync("bootstrap").AsTask().WaitAsync(RaceCoordinationTimeout);

        await AddClusterWithFixedDiscoveryAsync(client,
            "candidate",
            child => child.UseTransport(candidateTransport),
            slot => slot.AllowDynamicContracts = true);

        Ensure(candidateTransport.ConnectCount >= 1,
            "the candidate supervisor must observe its first remote failure before publication completes");
        Ensure(candidateTransport.DisposeCount == 0,
            "ordinary remote unavailability must not roll back the locally committed Add transaction");
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "remote failure in a published child must not terminate the parent runtime");
        Ensure(client.GetClusterReadiness("candidate") == SharpLinkReadinessState.NotReady,
            "the published unavailable child must report NotReady independently from Add success");
        Ensure(client.Readiness == SharpLinkReadinessState.Degraded,
            "a ready bootstrap plus an unavailable added child must project Degraded readiness");
    }

    [Test]
    public async Task RunningLegacyConnectSnapshotShouldNotGrowWhenAddPublishes()
    {
        var bootstrapTransport = new ControlledMutationTransportFactory(blockConnect: true);
        var candidateTransport = new ControlledMutationTransportFactory(blockConnect: true);
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(bootstrapTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var legacyConnect = client.ConnectAsync().AsTask();
        await bootstrapTransport.ConnectStarted.Task.WaitAsync(RaceCoordinationTimeout);
        await client.StartAsync();
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running &&
               client.State == SharpLinkMultiClusterState.Connecting,
            "the mixed compatibility path must expose Running + aggregate Connecting before Add");

        var add = AddClusterWithFixedDiscoveryAsync(client,
            "candidate",
            child => child.UseTransport(candidateTransport),
            slot => slot.AllowDynamicContracts = true).AsTask();
        await candidateTransport.ConnectStarted.Task.WaitAsync(RaceCoordinationTimeout);
        await add.WaitAsync(RaceCoordinationTimeout);
        Ensure(!legacyConnect.IsCompleted,
            "publishing a later slot must not rewrite the already-captured compatibility connect batch");

        bootstrapTransport.ReleaseConnect();
        await legacyConnect.WaitAsync(RaceCoordinationTimeout);

        Ensure(client.State == SharpLinkMultiClusterState.Degraded,
            "legacy connect completion must project aggregate state from the latest snapshot");
        Ensure(client.Readiness == SharpLinkReadinessState.Degraded,
            "latest readiness must include the newly published not-ready slot");
        Ensure(client.GetClusterReadiness("candidate") == SharpLinkReadinessState.NotReady,
            "the later slot must remain outside the old compatibility connect batch");

        candidateTransport.ReleaseConnect();
        await client.WaitForReadyAsync("candidate").AsTask().WaitAsync(RaceCoordinationTimeout);
    }

    [Test]
    public async Task FailedLegacyConnectMustNotStopAClusterAddedAfterItsSnapshot()
    {
        var bootstrapTransport = new ControlledMutationTransportFactory(
            blockConnect: true,
            connectFailure: new InvalidOperationException("controlled captured connect failure"));
        var candidateTransport = new ControlledMutationTransportFactory();
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(bootstrapTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var legacyConnect = client.ConnectAsync().AsTask();
        await bootstrapTransport.ConnectStarted.Task.WaitAsync(RaceCoordinationTimeout);
        await client.StartAsync();
        await AddClusterWithFixedDiscoveryAsync(client,
            "candidate",
            child => child.UseTransport(candidateTransport),
            slot => slot.AllowDynamicContracts = true);
        await client.WaitForReadyAsync("candidate").AsTask().WaitAsync(RaceCoordinationTimeout);

        bootstrapTransport.ReleaseConnect();
        var failure = await CaptureExceptionAsync(legacyConnect.WaitAsync(RaceCoordinationTimeout));

        Ensure(failure is InvalidOperationException
        { Message: "controlled captured connect failure" },
            "the original compatibility caller must still observe its captured batch failure");
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "a captured compatibility failure must not fault an already-running coordinator");
        Ensure(candidateTransport.DisposeCount == 0 &&
               client.GetClusterReadiness("candidate") == SharpLinkReadinessState.Ready,
            "the failed old batch must not stop or retire a slot published after its snapshot");
        Ensure(client.Readiness == SharpLinkReadinessState.Degraded,
            "the latest snapshot should remain usable with the old unavailable slot and the new ready slot");
    }
}

internal sealed class CancelOnConnectTransportFactory(
    CancellationTokenSource callerCancellation) : IClientTransportFactory
{
    private int _disposeCount;

    internal TaskCompletionSource ConnectStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int DisposeCount => Volatile.Read(ref _disposeCount);

    public async ValueTask<ITransportConnection> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        ConnectStarted.TrySetResult();
        callerCancellation.Cancel();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("cancelled candidate connect should not continue");
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }
}
