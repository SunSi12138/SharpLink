using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientPublicLifecycleTests
{
    [Test]
    public async Task StartShouldRunWhileRemoteIsUnavailableAndShutdownWaitMustNotStopClient()
    {
        await using ISharpLinkClient client = new SharpLinkClient(
            new AlwaysFailingTransportFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

        await client.StartAsync();

        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "local runtime must reach Running without remote readiness");
        Ensure(client.Readiness == SharpLinkReadinessState.NotReady,
            "remote connection failure must remain a readiness condition");
        Ensure(client.ClusterState != SharpLinkClusterState.Ready,
            "an unavailable endpoint must not be projected as a ready cluster");
        Ensure(client.State != SharpLinkConnectionState.Faulted,
            "client-owned startup must not expose remote unavailability as a terminal legacy state");

        var shutdownWait = client.WaitForShutdownAsync();
        Ensure(!shutdownWait.IsCompleted,
            "WaitForShutdownAsync must not request shutdown itself");

        using (var readinessCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await EnsureCancelledAsync(client.WaitForReadyAsync(readinessCancellation.Token).AsTask());
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "canceling one readiness waiter must not change client lifecycle");

        await client.StopAsync();
        await shutdownWait.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Stopped,
            "shutdown wait must complete only after the owned stop operation terminates");
    }

    [Test]
    public async Task InitialRemoteFailureShouldRecoverUnderClientOwnedSupervisor()
    {
        var transport = new RecoveringTransportFactory();
        await using ISharpLinkClient client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

        await client.StartAsync();
        await client.WaitForReadyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(transport.ConnectCount >= 2,
            "the client-owned supervisor must retry an initial remote connection failure");
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "connectivity recovery must not replace the Running lifecycle");
        Ensure(client.Readiness == SharpLinkReadinessState.Ready,
            "successful recovery must publish readiness independently");
        Ensure(client.ClusterState == SharpLinkClusterState.Ready,
            "successful recovery must publish cluster connectivity");
    }

    [Test]
    public async Task MultiClusterShouldRemainRunningWhenOneClusterIsUnavailable()
    {
        var readyTransport = new TestClientTransportFactory();
        var unavailableTransport = new AlwaysFailingTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster(
                "ready",
                child => child.UseTransport(readyTransport),
                slot => slot.AllowDynamicContracts = true)
            .AddCluster(
                "unavailable",
                child => child.UseTransport(unavailableTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StartAsync();
        await client.WaitForReadyAsync("ready").AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "one unavailable child must not fault the coordinator lifecycle");
        Ensure(client.Readiness == SharpLinkReadinessState.Degraded,
            "mixed child readiness must be reported as Degraded");
        Ensure(client.GetClusterReadiness("ready") == SharpLinkReadinessState.Ready,
            "ready child readiness must remain independently observable");
        Ensure(client.GetClusterReadiness("unavailable") == SharpLinkReadinessState.NotReady,
            "unavailable child readiness must remain independently observable");
        Ensure(client.GetClusterRuntimeState("ready") == SharpLinkClusterState.Ready,
            "ready child cluster state must remain independently observable");

        using (var readinessCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await EnsureCancelledAsync(
                client.WaitForReadyAsync("unavailable", readinessCancellation.Token).AsTask());
        }
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "canceling a scoped readiness wait must not affect coordinator lifecycle");

        var shutdownWait = client.WaitForShutdownAsync();
        Ensure(!shutdownWait.IsCompleted,
            "coordinator shutdown wait must not request shutdown");
        await client.StopAsync();
        await shutdownWait.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task RuntimeSlotMutationsShouldNotChangeCoordinatorLifecycle()
    {
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster(
                "primary",
                child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.StartAsync();
        await client.WaitForReadyAsync("primary").AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var addedTransport = new BlockingSuccessTransportFactory();
        var add = client.AddClusterAsync(
            "secondary",
            child => child.UseTransport(addedTransport),
            slot => slot.AllowDynamicContracts = true).AsTask();
        await addedTransport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "runtime add must not replace the coordinator lifecycle while its candidate connects");
        addedTransport.ReleaseConnect();
        await add.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "runtime add must leave the coordinator Running after publication");
        await client.WaitForReadyAsync("secondary").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await client.WaitForReadyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.GetClusterReadiness("secondary") == SharpLinkReadinessState.Ready,
            "runtime add must publish a child whose local runtime participates in readiness");
        Ensure(client.Readiness == SharpLinkReadinessState.Ready,
            "runtime add must preserve aggregate readiness once the candidate is connected");

        var replacementTransport = new BlockingSuccessTransportFactory();
        var replace = client.ReplaceClusterAsync(
            "secondary",
            child => child.UseTransport(replacementTransport),
            TimeSpan.FromSeconds(2)).AsTask();
        await replacementTransport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "runtime replace must not replace the coordinator lifecycle while its candidate connects");
        replacementTransport.ReleaseConnect();
        await replace.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "runtime replace must leave the coordinator Running after publication");
        await client.WaitForReadyAsync("secondary").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await client.WaitForReadyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.GetClusterReadiness("secondary") == SharpLinkReadinessState.Ready,
            "runtime replace must publish a child whose local runtime participates in readiness");
        Ensure(client.Readiness == SharpLinkReadinessState.Ready,
            "runtime replace must preserve aggregate readiness once the candidate is connected");

        var removal = await client.RemoveClusterAsync(
            "secondary",
            TimeSpan.FromSeconds(2));
        Ensure(removal.Succeeded,
            "runtime remove must publish the requested slot removal");
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "runtime remove must leave the coordinator Running");
    }

    [Test]
    public async Task FailedLegacyConnectAfterStartShouldNotTearDownRunningCoordinator()
    {
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster(
                "ready",
                child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .AddCluster(
                "unavailable",
                child => child.UseTransport(new AlwaysFailingTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StartAsync();
        await client.WaitForReadyAsync("ready").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await EnsureFailsAsync(client.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "a failed compatibility connect after StartAsync must not stop the coordinator runtime");
        Ensure(client.Readiness == SharpLinkReadinessState.Degraded,
            "a failed compatibility connect after StartAsync must remain a readiness condition");
        Ensure(client.GetClusterReadiness("ready") == SharpLinkReadinessState.Ready,
            "a failed compatibility connect must not tear down an already-ready child");
        Ensure(client.GetClusterRuntimeState("ready") == SharpLinkClusterState.Ready,
            "the ready child must remain connected after another child connect attempt fails");
        Ensure(client.GetClusterRuntimeState("unavailable") != SharpLinkClusterState.Stopped,
            "remote unavailability must not stop the unavailable child runtime either");
    }

    private static async Task EnsureFailsAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            return;
        }
        throw new Exception("expected operation failure");
    }

    private static async Task EnsureCancelledAsync(Task task)
    {
        try
        {
            await task;
            throw new Exception("expected wait cancellation");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class AlwaysFailingTransportFactory : IClientTransportFactory
    {
        private int _connectCount;

        internal int ConnectCount => Volatile.Read(ref _connectCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            return ValueTask.FromException<ITransportConnection>(
                new IOException("test endpoint is unavailable"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecoveringTransportFactory : IClientTransportFactory
    {
        private readonly TestClientTransportFactory _inner = new();
        private int _connectCount;

        internal int ConnectCount => Volatile.Read(ref _connectCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _connectCount) == 1)
            {
                return ValueTask.FromException<ITransportConnection>(
                    new IOException("first connection attempt failed"));
            }
            return _inner.ConnectAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class BlockingSuccessTransportFactory : IClientTransportFactory
    {
        private readonly TestClientTransportFactory _inner = new();
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        internal void ReleaseConnect() => _release.TrySetResult();
    }
}
