using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterMutationConcurrencyTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task ConnectingCoordinatorShouldRejectRuntimeMutationWithoutPublishingCandidate()
    {
        var blocked = new BlockingTransportFactory();
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(blocked))
            .Build();

        var connecting = client.ConnectAsync().AsTask();
        await blocked.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var failure = await CaptureExceptionAsync(AddClusterWithFixedDiscoveryAsync(client,
            "plugins",
            child => child.UseTransport(rejectedTransport),
            slot => slot.AllowDynamicContracts = true).AsTask());

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("connecting", StringComparison.OrdinalIgnoreCase),
            "runtime slot mutation must be rejected while the coordinator is Connecting");
        Ensure(rejectedTransport.DisposeCount == 1,
            "Connecting rejection must release the unbuilt candidate resources");
        await client.StopAsync();
        await EnsureThrows<OperationCanceledException>(async () => await connecting);
        Ensure(client.State == SharpLinkMultiClusterState.Stopped,
            "the rejected mutation must not interfere with coordinator shutdown");
    }

    [Test]
    public async Task ConcurrentSameKeyAddsShouldPublishOneCandidateAndDisposeTheLoser()
    {
        var winnerTransport = new ControlledMutationTransportFactory(blockConnect: true);
        var loserTransport = new ControlledMutationTransportFactory();
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync();

        var winner = AddClusterWithFixedDiscoveryAsync(client,
            "candidate",
            child => child.UseTransport(winnerTransport),
            slot => slot.AllowDynamicContracts = true).AsTask();
        await winnerTransport.ConnectStarted.Task.WaitAsync(RaceCoordinationTimeout);
        var loser = AddClusterWithFixedDiscoveryAsync(client,
            "candidate",
            child => child.UseTransport(loserTransport),
            slot => slot.AllowDynamicContracts = true).AsTask();
        await Task.Delay(50);
        Ensure(!loser.IsCompleted,
            "v1 must serialize a second same-key mutation behind the in-flight candidate");

        winnerTransport.ReleaseConnect();
        await winner.WaitAsync(RaceCoordinationTimeout);
        var loserFailure = await CaptureExceptionAsync(loser.WaitAsync(RaceCoordinationTimeout));

        Ensure(loserFailure is InvalidOperationException exception &&
               exception.Message.Contains("already configured", StringComparison.Ordinal),
            "the serialized losing add must observe the committed duplicate key");
        Ensure(winnerTransport.ConnectCount == 1 && winnerTransport.DisposeCount == 0,
            "the winning candidate must be connected once and remain coordinator-owned");
        Ensure(loserTransport.ConnectCount == 0 && loserTransport.DisposeCount == 1,
            "the losing unbuilt candidate must never connect and must release its transport");
    }

    [Test]
    public async Task ThrowingMutationLoggerShouldNotFailOrStrandLaterMutations()
    {
        var builder = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true);
        builder.UseLoggerFactoryIfUnset(new ThrowingWriteLoggerFactory());
        await using var client = builder.Build();

        await AddClusterWithFixedDiscoveryAsync(client,
            "first",
            child => child.UseTransport(new TestClientTransportFactory()),
            slot => slot.AllowDynamicContracts = true);
        await AddClusterWithFixedDiscoveryAsync(client,
            "second",
            child => child.UseTransport(new TestClientTransportFactory()),
            slot => slot.AllowDynamicContracts = true);

        Ensure(client.GetClusterState("first") == SharpLinkConnectionState.Created &&
               client.GetClusterState("second") == SharpLinkConnectionState.Created,
            "application logger failures must not change mutation results or strand the semaphore");
    }

    [Test]
    public async Task StopRacingRuntimeAddShouldCancelAndDisposeThePendingCandidate()
    {
        var candidateTransport = new ControlledMutationTransportFactory(blockConnect: true);
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync();

        var add = AddClusterWithFixedDiscoveryAsync(client,
            "candidate",
            child => child.UseTransport(candidateTransport),
            slot => slot.AllowDynamicContracts = true).AsTask();
        await candidateTransport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop = client.StopAsync().AsTask();

        await EnsureThrows<OperationCanceledException>(async () => await add);
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.State == SharpLinkMultiClusterState.Stopped,
            "global Stop must win a race with an unpublished runtime add");
        Ensure(candidateTransport.DisposeCount == 1,
            "Stop-raced candidate resources must be disposed exactly once");
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState("candidate");
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task DegradedCoordinatorShouldConnectCandidateBeforeRuntimeAddPublication()
    {
        var candidateTransport = new ControlledMutationTransportFactory();
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        typeof(SharpLinkMultiClusterClient)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, (int)SharpLinkMultiClusterState.Degraded);

        await AddClusterWithFixedDiscoveryAsync(client,
            "candidate",
            child => child.UseTransport(candidateTransport),
            slot => slot.AllowDynamicContracts = true);

        Ensure(candidateTransport.ConnectCount == 1,
            "a Degraded coordinator must connect a runtime candidate before publication");
        Ensure(client.GetClusterState("candidate") == SharpLinkConnectionState.Ready,
            "the published candidate must expose its connected state");
    }

    [Test]
    [Arguments(SharpLinkMultiClusterState.Draining)]
    [Arguments(SharpLinkMultiClusterState.Stopped)]
    [Arguments(SharpLinkMultiClusterState.Faulted)]
    public async Task TerminalCoordinatorStateShouldRejectRuntimeMutation(
        SharpLinkMultiClusterState terminalState)
    {
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        typeof(SharpLinkMultiClusterClient)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, (int)terminalState);

        var failure = await CaptureExceptionAsync(AddClusterWithFixedDiscoveryAsync(client,
            "candidate",
            child => child.UseTransport(rejectedTransport),
            slot => slot.AllowDynamicContracts = true).AsTask());

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains(terminalState.ToString(), StringComparison.Ordinal),
            "terminal coordinator states must reject runtime slot mutations explicitly");
        Ensure(rejectedTransport.DisposeCount == 1,
            "a candidate builder rejected by a terminal state must release its resources");
    }

    [Test]
    public async Task CancelledReadyAddShouldRollbackCandidateWithoutPublishingItsSlot()
    {
        var bootstrapTransport = new ControlledMutationTransportFactory();
        var candidateTransport = new ControlledMutationTransportFactory(blockConnect: true);
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(bootstrapTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();

        var add = AddClusterWithFixedDiscoveryAsync(client,
            "candidate",
            child => child.UseTransport(candidateTransport),
            slot => slot.AllowDynamicContracts = true,
            cancellation.Token).AsTask();
        await candidateTransport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await EnsureThrows<OperationCanceledException>(async () => await add);
        Ensure(candidateTransport.DisposeCount == 1,
            "cancellation before publication must stop and dispose the connected candidate generation");
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState("candidate");
            return Task.CompletedTask;
        });
        Ensure(client.GetClusterState("bootstrap") == SharpLinkConnectionState.Ready,
            "candidate cancellation must leave the existing public snapshot unchanged");
    }

    [Test]
    public async Task CreatedAddCancellationDuringPreparationShouldRollbackBeforePublication()
    {
        var candidateTransport = new ControlledMutationTransportFactory();
        using var cancellation = new CancellationTokenSource();
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var failure = await CaptureExceptionAsync(AddClusterWithFixedDiscoveryAsync(client,
            "candidate",
            child => child.UseEndpoints(
                new CancellingEndpointEnumerable(
                    cancellation,
                    Endpoint("candidate", 6501)),
                _ => candidateTransport),
            slot => slot.AllowDynamicContracts = true,
            cancellation.Token).AsTask());

        Ensure(failure is OperationCanceledException,
            "Created-state cancellation during synchronous preparation must reach the caller");
        Ensure(candidateTransport.ConnectCount == 0 && candidateTransport.DisposeCount == 1,
            "the prepared Created candidate must be disposed without connecting");
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState("candidate");
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task CreatedReplaceCancellationDuringPreparationShouldKeepTheOldSlot()
    {
        var oldTransport = new ControlledMutationTransportFactory();
        var candidateTransport = new ControlledMutationTransportFactory();
        using var cancellation = new CancellationTokenSource();
        await using var client = CreateDynamicBuilder()
            .AddCluster("dynamic", child => child.UseTransport(oldTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var failure = await CaptureExceptionAsync(client.ReplaceClusterAsync(
            "dynamic",
            child => child.UseEndpoints(
                new CancellingEndpointEnumerable(
                    cancellation,
                    Endpoint("candidate", 6502)),
                _ => candidateTransport),
            TimeSpan.Zero,
            cancellation.Token).AsTask());

        Ensure(failure is OperationCanceledException,
            "Created-state replacement cancellation during preparation must reach the caller");
        Ensure(candidateTransport.ConnectCount == 0 && candidateTransport.DisposeCount == 1,
            "the cancelled replacement candidate must be disposed without connecting");
        Ensure(oldTransport.DisposeCount == 0 &&
               client.GetClusterState("dynamic") == SharpLinkConnectionState.Created,
            "replacement cancellation must keep the old slot published and owned by the coordinator");
    }
}
