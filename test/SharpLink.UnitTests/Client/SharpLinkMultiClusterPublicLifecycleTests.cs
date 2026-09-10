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
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();

        await client.StartAsync();
        await client.WaitForReadyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await AddClusterWithFixedDiscoveryAsync(
            client,
            "payments",
            child => child.UseTransport(new TestClientTransportFactory()),
            slot => slot.AllowDynamicContracts = true,
            manifests: [],
            routes: []);
        await client.WaitForReadyAsync("payments").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await client.WaitForReadyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "adding a cluster must not change the coordinator lifecycle");
        Ensure(client.GetClusterRuntimeState("payments") == SharpLinkClusterState.Ready,
            "runtime-added child should be started before it is published ready");

        await client.ReplaceClusterAsync(
            "payments",
            child => child.UseTransport(new TestClientTransportFactory()),
            TimeSpan.Zero);
        await client.WaitForReadyAsync("payments").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await client.WaitForReadyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "replacing a cluster must not change the coordinator lifecycle");
        Ensure(client.GetClusterReadiness("payments") == SharpLinkReadinessState.Ready,
            "replacement child should enter the running/readiness lifecycle before publication");
    }

    private sealed class FailingTransportFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(
                new NotSupportedException("controlled cluster connection failure"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
