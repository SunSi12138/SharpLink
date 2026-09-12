using System.Collections.Frozen;
using SharpLink.Abstractions;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterMutationResultTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task ReplaceMissingClusterShouldReturnNotFoundAndDisposeRejectedBuilder()
    {
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var result = await client.ReplaceClusterAsync(
            "missing",
            child => child.UseTransport(rejectedTransport),
            TimeSpan.Zero);

        Ensure(result is
        {
            Succeeded: false,
            Published: false,
            FailureCode: SharpLinkClusterMutationFailureCode.NotFound
        },
            "valid missing replacement targets must be machine-readable without exception control flow");
        Ensure(rejectedTransport.DisposeCount == 1,
            "a pre-preparation replacement rejection must dispose unbuilt builder resources");
    }

    [Test]
    public async Task RemoveMissingClusterShouldReturnNotFound()
    {
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var result = await client.RemoveClusterAsync("missing", TimeSpan.Zero);

        Ensure(result is
        {
            Succeeded: false,
            FailureCode: SharpLinkClusterMutationFailureCode.NotFound,
            ReferencesReleased: false,
            ForcedStop: false
        },
            "valid missing removal targets must be a structured rejection");
        Ensure(client.GetClusterState("bootstrap") == SharpLinkConnectionState.Created,
            "a rejected removal must not mutate the public snapshot");
    }

    [Test]
    public async Task ConnectingReplaceAndRemoveShouldReturnBusy()
    {
        var blocked = new BlockingTransportFactory();
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(blocked))
            .Build();
        var connecting = client.ConnectAsync().AsTask();
        await blocked.ConnectStarted.Task.WaitAsync(RaceCoordinationTimeout);

        var replace = await client.ReplaceClusterAsync(
            "orders",
            child => child.UseTransport(new ControlledMutationTransportFactory()),
            TimeSpan.Zero);
        var remove = await client.RemoveClusterAsync("orders", TimeSpan.Zero);

        Ensure(replace.FailureCode == SharpLinkClusterMutationFailureCode.Busy && !replace.Published,
            "replacement must report Busy while the legacy coordinator is connecting");
        Ensure(remove.FailureCode == SharpLinkClusterMutationFailureCode.Busy && !remove.Succeeded,
            "removal must report Busy while the legacy coordinator is connecting");
        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Connecting,
            "Busy rejections must not clear the active lifecycle operation or unpublish the slot");

        await client.StopAsync();
        await EnsureThrows<OperationCanceledException>(async () => await connecting);
    }

    [Test]
    public async Task StoppedReplaceAndRemoveShouldReturnLifecycleClosed()
    {
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.StopAsync();

        var replace = await client.ReplaceClusterAsync(
            "bootstrap",
            child => child.UseTransport(new ControlledMutationTransportFactory()),
            TimeSpan.Zero);
        var remove = await client.RemoveClusterAsync("bootstrap", TimeSpan.Zero);

        Ensure(replace.FailureCode == SharpLinkClusterMutationFailureCode.LifecycleClosed && !replace.Published,
            "replacement must expose terminal lifecycle rejection structurally");
        Ensure(remove.FailureCode == SharpLinkClusterMutationFailureCode.LifecycleClosed && !remove.Succeeded,
            "removal must expose terminal lifecycle rejection structurally");
    }

    [Test]
    public async Task LocalSharpLinkReplacementFailureShouldRemainExceptionalAndKeepOldRoute()
    {
        var oldTransport = new ControlledMutationTransportFactory();
        var candidateTransport = new ControlledMutationTransportFactory();
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(oldTransport))
            .Build();
        await client.ConnectAsync();
        var oldProxy = (OrdersProxy)client.Get<IOrdersContract>();

        var failure = await CaptureExceptionAsync(client.ReplaceClusterAsync(
            "orders",
            child => child
                .UseProtocol(static options => options.MaxMetadataBytes = 1)
                .UseAuthenticator(SharpLinkAuthenticator.CreateClient(
                    static _ => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[2])))
                .UseTransport(candidateTransport),
            TimeSpan.Zero).AsTask());
        var retainedProxy = (OrdersProxy)client.Get<IOrdersContract>();

        Ensure(failure is SharpLinkException
        {
            Code: SharpLinkErrorCode.ResourceExhausted
        } exception && exception.Message.Contains("Authentication payload exceeds", StringComparison.Ordinal),
            "local SharpLink configuration failures must remain exceptional instead of becoming CandidateUnavailable");
        Ensure(ReferenceEquals(oldProxy.Channel, retainedProxy.Channel),
            "a local pre-publication SharpLink failure must leave the old route authoritative");
        Ensure(candidateTransport.DisposeCount == 1 && oldTransport.DisposeCount == 0,
            "local candidate failure rollback must dispose only the rejected candidate");
    }

    [Test]
    public async Task UnexpectedReplacementCandidateFailureShouldRemainExceptionalAndKeepOldRoute()
    {
        var oldTransport = new ControlledMutationTransportFactory();
        var invariantCandidate = new ControlledMutationTransportFactory(
            connectFailure: new InvalidOperationException("controlled replacement invariant failure"));
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(oldTransport))
            .Build();
        await client.ConnectAsync();
        var oldProxy = (OrdersProxy)client.Get<IOrdersContract>();

        var failure = await CaptureExceptionAsync(client.ReplaceClusterAsync(
            "orders",
            child => child.UseTransport(invariantCandidate),
            TimeSpan.Zero).AsTask());
        var retainedProxy = (OrdersProxy)client.Get<IOrdersContract>();

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("controlled replacement invariant failure", StringComparison.Ordinal),
            "unexpected candidate invariant failures must remain exceptional instead of becoming CandidateUnavailable");
        Ensure(ReferenceEquals(oldProxy.Channel, retainedProxy.Channel),
            "an exceptional pre-publication candidate failure must leave the old route authoritative");
        Ensure(invariantCandidate.DisposeCount == 1 && oldTransport.DisposeCount == 0,
            "exceptional candidate rollback must dispose only the rejected candidate");
    }

    [Test]
    public async Task SuccessfulReplacementCanReportPendingOldCleanup()
    {
        var oldClient = new BlockingRetiredClient();
        var oldSlot = new SharpLinkClusterSlot(
            "runtime",
            oldClient,
            AllowDynamicContracts: true,
            ConfiguredConnectionBudget: 1);
        var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions
            {
                MaxClusters = 2,
                MaxTotalConfiguredConnections = 2
            },
            new[] { oldSlot }.ToFrozenDictionary(static slot => slot.Key),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            [],
            configuredConnectionBudget: 1);
        try
        {
            var result = await client.ReplaceClusterAsync(
                "runtime",
                child => child.DisableRequestTimeout().UseTransport(new ControlledMutationTransportFactory()),
                TimeSpan.Zero);
            await oldClient.StopStarted.Task.WaitAsync(RaceCoordinationTimeout);

            Ensure(result is
            {
                Succeeded: true,
                Published: true,
                FailureCode: SharpLinkClusterMutationFailureCode.None,
                ReferencesReleased: false,
                ForcedStop: true
            },
                "replacement publication success must remain distinct from pending retirement cleanup");
            Ensure(client.GetClusterState("runtime") == SharpLinkConnectionState.Created,
                "the replacement must remain authoritative while old cleanup continues");
        }
        finally
        {
            oldClient.ReleaseStop();
            await client.DisposeAsync();
        }
    }
}
