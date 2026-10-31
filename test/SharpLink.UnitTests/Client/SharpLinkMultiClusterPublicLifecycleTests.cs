using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterPublicLifecycleTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task StartAsyncShouldAllowPartialReadinessAndFailedCompatibilityConnect()
    {
        var unavailable = new FailingTransportFactory();
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .AddCluster(
                "offline",
                child => child.UseTransport(unavailable),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StartAsync();
        await client.WaitForReadyAsync("orders").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(
            () => client.Readiness == SharpLinkReadinessState.Degraded,
            "one ready and one unavailable cluster should publish Degraded readiness");

        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "partial remote availability must not fault the local coordinator");
        Ensure(client.GetClusterReadiness("orders") == SharpLinkReadinessState.Ready,
            "healthy cluster should remain independently ready");
        Ensure(client.GetClusterReadiness("offline") == SharpLinkReadinessState.NotReady,
            "failed cluster should remain independently not ready");

        var compatibilityFailure = await CaptureExceptionAsync(client.ConnectAsync().AsTask());
        Ensure(compatibilityFailure is not null,
            "legacy all-cluster ConnectAsync should still report the unavailable required slot");
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "failed compatibility ConnectAsync after StartAsync must not stop the coordinator");
        Ensure(client.GetClusterReadiness("orders") == SharpLinkReadinessState.Ready,
            "failed compatibility ConnectAsync must not tear down an already-ready child");

        await client.StopAsync();
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Stopped,
            "coordinator should stop after partial-readiness operation");
    }

    [Test]
    public async Task RuntimeAddAndReplaceAfterStartShouldEnterChildRuntimeLifecycle()
    {
        await using var addedTransport = new ControlledMutationTransportFactory(blockConnect: true);
        await using var replacementTransport = new ControlledMutationTransportFactory(blockConnect: true);
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();

        await client.StartAsync();
        await client.WaitForReadyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Task<SharpLinkClusterAddResult>? addOperation = null;
        Task<SharpLinkClusterReplacementResult>? replacement = null;
        try
        {
            addOperation = AddClusterWithFixedDiscoveryAsync(
                client,
                "payments",
                child => child.UseTransport(addedTransport),
                slot => slot.AllowDynamicContracts = true,
                manifests: [],
                routes: []).AsTask();
            var add = await addOperation.WaitAsync(RaceCoordinationTimeout);
            Ensure(add.Succeeded,
                "runtime Add should publish its locally started child without waiting for remote readiness");
            var addedChild = (ISharpLinkClient)GetChildChannel(client, "payments");
            Ensure(addedChild.LifecycleState == SharpLinkClientLifecycleState.Running,
                "runtime-added child should be locally Running before its slot is published");
            await addedTransport.ConnectStarted.Task.WaitAsync(RaceCoordinationTimeout);
            Ensure(ReferenceEquals(addedChild, GetChildChannel(client, "payments")) &&
                   addedChild.LifecycleState == SharpLinkClientLifecycleState.Running,
                "the published Running child should own the held connection attempt");
            Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
                "adding a cluster must not change the coordinator lifecycle");
            Ensure(client.GetClusterReadiness("payments") == SharpLinkReadinessState.NotReady,
                "the published child must remain NotReady while its remote dial is held");

            addedTransport.ReleaseConnect();
            await client.WaitForReadyAsync("payments").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await client.WaitForReadyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            // Local startup and remote readiness are separate domains. A readiness observation
            // need not wait for the legacy connection-state projection to finish publishing.
            Ensure(client.GetClusterReadiness("payments") == SharpLinkReadinessState.Ready,
                "runtime-added child should become remotely ready after its dial is released");

            replacement = client.ReplaceClusterAsync(
                "payments",
                child => child.UseTransport(replacementTransport),
                TimeSpan.Zero).AsTask();
            await replacementTransport.ConnectStarted.Task.WaitAsync(RaceCoordinationTimeout);
            Ensure(!replacement.IsCompleted,
                "replacement must wait for candidate readiness before swapping the published slot");
            Ensure(ReferenceEquals(addedChild, GetChildChannel(client, "payments")),
                "the old child must remain published while the replacement dial is held");
            Ensure(client.GetClusterReadiness("payments") == SharpLinkReadinessState.Ready,
                "the old child must remain ready while the replacement candidate is connecting");

            replacementTransport.ReleaseConnect();
            var replace = await replacement.WaitAsync(RaceCoordinationTimeout);
            Ensure(replace.Succeeded && replace.Published,
                "a ready replacement candidate should commit its slot publication");
            var replacementChild = (ISharpLinkClient)GetChildChannel(client, "payments");
            Ensure(!ReferenceEquals(addedChild, replacementChild),
                "replacement should publish a distinct child runtime");
            Ensure(replacementChild.LifecycleState == SharpLinkClientLifecycleState.Running,
                "replacement child should be locally Running when its slot is published");
            await client.WaitForReadyAsync("payments").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await client.WaitForReadyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
                "replacing a cluster must not change the coordinator lifecycle");
            Ensure(client.GetClusterReadiness("payments") == SharpLinkReadinessState.Ready,
                "replacement child should enter the running/readiness lifecycle before publication");
        }
        finally
        {
            addedTransport.ReleaseConnect();
            replacementTransport.ReleaseConnect();
            if (addOperation is not null)
                await LongRunningTestWorker.JoinAsync(addOperation, RaceCoordinationTimeout);
            if (replacement is not null)
                await LongRunningTestWorker.JoinAsync(replacement, RaceCoordinationTimeout);
        }
    }

    private sealed class FailingTransportFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(
                new NotSupportedException("controlled cluster connection failure"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
