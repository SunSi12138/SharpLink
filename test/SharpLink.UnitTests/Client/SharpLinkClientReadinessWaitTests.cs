using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientReadinessSharedSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientReadinessWaitSupport;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientReadinessWaitTests
{
    [Test]
    public async Task SatisfiedFixedReadinessWaitShouldCompleteSynchronously()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();

        var wait = client.WaitForReadinessAsync(1);

        Ensure(wait.IsCompletedSuccessfully,
            "an already-satisfied readiness wait must use the synchronous ValueTask fast path");
        var observed = await wait;
        Ensure(observed == client.GetReadinessSnapshot() && observed.MeetsTarget,
            "the synchronous wait must return the exact satisfying publication");
        Ensure(transport.ConnectCount == 1,
            "an already-satisfied wait must not start another connection attempt");
    }

    [Test]
    public async Task FixedClientShouldRejectImpossibleThresholdBeforeConnectingOrCancellation()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = CaptureException(() => client.WaitForReadinessAsync(2, cancellation.Token));

        Ensure(failure is ArgumentOutOfRangeException { ParamName: "minimumReadyEndpoints" },
            "fixed readiness must reject a threshold above its configured maximum");
        Ensure(transport.ConnectCount == 0,
            "threshold validation must fail before cancellation handling or connection startup");
    }

    [Test]
    public async Task PreCanceledWaitShouldWinOverAnAlreadySatisfiedSnapshot()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = CaptureException(() => client.WaitForReadinessAsync(1, cancellation.Token));

        Ensure(failure is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token,
            "entry cancellation must be observed before the satisfied fast path");
        Ensure(client.State == SharpLinkConnectionState.Ready && transport.ConnectCount == 1,
            "canceling a readiness observation must not disturb the ready client");
    }

    [Test]
    public async Task CancelingOneReadinessWaitShouldNotCancelTheSharedConnectOrAnotherWaiter()
    {
        var transport = new BlockingInitialTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        using var cancellation = new CancellationTokenSource();

        var canceledWaiter = client.WaitForReadinessAsync(1, cancellation.Token).AsTask();
        await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var survivingWaiter = client.WaitForReadinessAsync(1).AsTask();
        cancellation.Cancel();

        var cancellationFailure = await CaptureExceptionAsync(canceledWaiter);
        Ensure(cancellationFailure is OperationCanceledException,
            "the canceled readiness waiter must observe only its caller cancellation");
        Ensure(!survivingWaiter.IsCompleted && client.State == SharpLinkConnectionState.Connecting,
            "another waiter and the shared client-owned connect must remain pending");
        Ensure(transport.ConnectCount == 1,
            "concurrent readiness waiters must join one shared initial connection attempt");

        transport.ReleaseConnect();
        var observed = await survivingWaiter.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(observed.MeetsTarget && observed.ReadyConnections == 1,
            "the surviving waiter must complete from the shared connection publication");
        Ensure(client.State == SharpLinkConnectionState.Ready,
            "caller cancellation must not stop or fault the client");
    }

    [Test]
    public async Task StoppingShouldWakeAPendingReadinessWaitWithConnectionClosed()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        await client.ConnectAsync();
        client.PublishReadinessFacts(new ClientReadinessFacts(
            ActiveEndpoints: 1,
            ReadyEndpoints: 0,
            ReadyConnections: 0,
            TargetReadyEndpoints: 1));
        var pendingPublication = client.ReadinessPublicationForTesting;

        var waiter = client.WaitForReadinessAsync(1).AsTask();
        Ensure(!waiter.IsCompleted,
            "the zero-ready testing publication must leave the readiness waiter pending");
        Ensure(!client.ReadySignalForTesting.IsCompleted,
            "zero readiness must install an incomplete level-triggered ready signal");

        await client.StopAsync();
        var failure = await CaptureExceptionAsync(waiter);

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "Stop must terminate a pending readiness waiter with the existing connection-closed taxonomy");
        Ensure(pendingPublication.Changed.Task.IsCompleted,
            "the Draining transition must complete the previous readiness generation");
        Ensure(client.ReadySignalForTesting.IsCompleted,
            "Stop must leave the ready signal permanently completed so terminal waiters cannot miss its pulse");
        Ensure(client.GetReadinessSnapshot() == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Stopped, 1, 0, 0, 1),
            "the fixed client must retain topology configuration while publishing terminal zero readiness");
    }

    [Test]
    public async Task StopAdmissionShouldRejectSatisfiedReadinessBeforeDrainingPublishes()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        await client.ConnectAsync();
        client.CloseStopAdmissionForTesting();
        Ensure(client.GetReadinessSnapshot().State == SharpLinkConnectionState.Ready,
            "closing Stop admission alone must leave the pre-Draining publication observable");

        var waiter = client.WaitForReadinessAsync(1).AsTask();

        Ensure(!waiter.IsCompletedSuccessfully,
            "a satisfied fast or slow readiness path must not return Ready after Stop admission closes");
        var stop = client.StopAsync().AsTask();
        var failure = await CaptureExceptionAsync(waiter);
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "the stop-racing readiness wait must terminate with the connection-closed taxonomy");
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task StoppingDuringInitialConnectivityShouldMapOnlyInternalCancellationToConnectionClosed()
    {
        var transport = new BlockingInitialTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        var waiter = client.WaitForReadinessAsync(1).AsTask();
        await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = client.StopAsync().AsTask();
        var failure = await CaptureExceptionAsync(waiter);

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "Client-owned shutdown cancellation during the joined ConnectAsync phase must use the readiness connection-closed taxonomy");
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.State == SharpLinkConnectionState.Stopped,
            "the mapped readiness failure must not interrupt the shared Stop operation");
    }
}
