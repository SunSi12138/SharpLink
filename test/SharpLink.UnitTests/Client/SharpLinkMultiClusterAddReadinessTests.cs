using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterAddReadinessTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task OnlyPublishedNotReadyAddedClusterShouldProjectNotReady()
    {
        var bootstrapTransport = new RetiringBootstrapTransportFactory();
        var candidateTransport = new ControlledMutationTransportFactory(blockConnect: true);
        await using var client = CreateDynamicBuilder()
            .AddCluster(
                "bootstrap",
                child => child.UseTransport(bootstrapTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        try
        {
            await client.StartAsync();
            await client.WaitForReadyAsync("bootstrap").AsTask().WaitAsync(RaceCoordinationTimeout);
            var bootstrap = (SharpLinkClient)GetChildChannel(client, "bootstrap");
            // This observes the original Remove-owned stop; it never starts another cleanup.
            var bootstrapShutdown = bootstrap.WaitForShutdownAsync();

            var removal = await client.RemoveClusterAsync("bootstrap", TimeSpan.FromSeconds(2));
            Ensure(removal.Succeeded && removal.FailureCode == SharpLinkClusterMutationFailureCode.None,
                "the bootstrap slot must be successfully unpublished before the added-only readiness assertion");
            Ensure(!client.TryGetClusterStatus("bootstrap", out _),
                "the removed bootstrap must no longer participate in the public snapshot");
            Ensure(!removal.ReferencesReleased && removal.ForcedStop,
                "the held factory must report coordinator-owned cleanup pending, not physical release");

            // Share the existing readiness watchdog with final verification, rather than
            // granting a fresh timeout for each background-cleanup observation.
            using var completionTimeout = new CancellationTokenSource(RaceCoordinationTimeout);
            await bootstrapTransport.DisposeStarted.Task.WaitAsync(completionTimeout.Token);

            var add = AddClusterWithFixedDiscoveryAsync(
                client,
                "candidate",
                child => child.UseTransport(candidateTransport),
                slot => slot.AllowDynamicContracts = true).AsTask();
            await candidateTransport.ConnectStarted.Task.WaitAsync(completionTimeout.Token);
            Ensure((await add.WaitAsync(completionTimeout.Token)).Succeeded,
                "the candidate must be published while bootstrap cleanup is pending");
            Ensure(client.TryGetClusterStatus("candidate", out _) && !client.TryGetClusterStatus("bootstrap", out _),
                "only the candidate may participate in the published readiness projection");

            Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
                "publishing a not-ready added cluster must keep the local coordinator Running");
            Ensure(client.GetClusterReadiness("candidate") == SharpLinkReadinessState.NotReady,
                "the added cluster must remain NotReady while its remote connect is pending");
            Ensure(client.Readiness == SharpLinkReadinessState.NotReady,
                "when the added not-ready cluster is the only published slot, aggregate readiness must be NotReady");

            candidateTransport.ReleaseConnect();
            await client.WaitForReadyAsync("candidate").AsTask().WaitAsync(completionTimeout.Token);
            Ensure(client.Readiness == SharpLinkReadinessState.Ready,
                "the published child supervisor must converge aggregate readiness without another ConnectAsync call");
            Ensure(!bootstrapShutdown.IsCompleted && !bootstrapTransport.DisposeCompleted.Task.IsCompleted,
                "readiness convergence must not pretend the held bootstrap factory has retired");

            bootstrapTransport.ReleaseDispose();
            await bootstrapShutdown.WaitAsync(completionTimeout.Token);
            Ensure(bootstrap.State == SharpLinkConnectionState.Stopped &&
                   bootstrapTransport.DisposeCount == 1 && bootstrapTransport.DisposeCompleted.Task.IsCompletedSuccessfully,
                "the original retirement must finish child and factory disposal exactly once before coordinator Stop");
            Ensure(bootstrapTransport.OutputCompletion.IsCompleted && !bootstrapTransport.OutputCompletion.IsFaulted,
                "the original transport observer must physically exit before coordinator Stop");
            Ensure(bootstrap.FrameworkTaskSnapshotForDiagnostics is
            { IsSealed: true, IsDrained: true, ActiveTasks: 0, RetainedFailures: 0 },
                "the removed child must join all its framework tasks before coordinator Stop");

            // Existing final cleanup may now join the coordinator, but cannot salvage the
            // bootstrap: its physical exit was independently verified above.
            await client.StopAsync().AsTask().WaitAsync(completionTimeout.Token);
            Ensure(((SharpLinkMultiClusterClient)client).FrameworkTaskSnapshotForDiagnostics is
            { IsSealed: true, IsDrained: true, ActiveTasks: 0, RetainedFailures: 0 } && bootstrapTransport.DisposeCount == 1,
                "final cleanup must join the coordinator's retired task without repeating bootstrap disposal");
        }
        finally
        {
            bootstrapTransport.ReleaseDispose();
            candidateTransport.ReleaseConnect();
        }
    }

    private sealed class RetiringBootstrapTransportFactory : IClientTransportFactory
    {
        private readonly TestClientTransportFactory _inner = new();
        private readonly TaskCompletionSource _disposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        internal TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal Task OutputCompletion => _inner.Connection.OutputCompletion;

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => _inner.ConnectAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            DisposeStarted.TrySetResult();
            await _disposeRelease.Task;
            await _inner.DisposeAsync();
            DisposeCompleted.TrySetResult();
        }

        internal void ReleaseDispose() => _disposeRelease.TrySetResult();
    }
}
