using System.Buffers.Binary;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientRemoteContractReadinessTests : SharpLinkMultiClusterClientTestBase
{
    private const long OrdersContractId = 8_101;
    private const long ObservationContractId = 8_102;
    private static readonly RpcHash128 MatchingHash = Manifest.Instance.RpcAssemblyHash;
    private static readonly RpcHash128 WrongHash = new(1, 2);

    [Test]
    public async Task WaitShouldEstablishInitialConnectivityAndObserveMatchingHandshake()
    {
        var transport = new TestClientTransportFactory();
        await using var client = CreateBuilder().UseTransport(transport).Build();

        await client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash)
            .AsTask().WaitAsync(RaceCoordinationTimeout);

        Ensure(client.State == SharpLinkConnectionState.Ready && transport.ConnectCount == 1,
            "remote contract readiness must join initial connectivity without requiring a separate Connect");
        Ensure(client.Get<IOrdersContract>() is OrdersProxy,
            "the observed exact handshake identity must permit synchronous contract acquisition");
    }

    [Test]
    public async Task MatchingReadyManifestShouldCompleteSynchronouslyWithoutReconnecting()
    {
        var transport = new TestClientTransportFactory();
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.ConnectAsync();

        var wait = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash);

        Ensure(wait.IsCompletedSuccessfully,
            "an already visible exact contract identity must complete without asynchronous scheduling");
        await wait;
        Ensure(transport.ConnectCount == 1, "readiness observation must not force reconnection");
    }

    [Test]
    public async Task MissingReadyContractShouldWaitForLaterManifestBeforeGet()
    {
        var transport = new TestClientTransportFactory(contractManifest: []);
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.ConnectAsync();

        var waiter = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash).AsTask();
        Ensure(!waiter.IsCompleted, "Ready transport alone must not satisfy missing remote identity");
        await PublishAndFenceAsync(transport.Connection, 1, [new(OrdersContractId, MatchingHash)]);
        await waiter.WaitAsync(RaceCoordinationTimeout);

        Ensure(client.Get<IOrdersContract>() is OrdersProxy,
            "later registration visibility must permit Get on the original data connection");
        Ensure(transport.ConnectCount == 1, "manifest propagation must preserve the existing connection");
    }

    [Test]
    public async Task ExistingWrongHashShouldFailClosedWithoutWaitingForAnotherGeneration()
    {
        var transport = new TestClientTransportFactory(contractManifest: [new(OrdersContractId, WrongHash)]);
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.ConnectAsync();

        var failure = await CaptureExceptionAsync(client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash)
            .AsTask().WaitAsync(RaceCoordinationTimeout));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "an advertised incompatible hash must fail closed instead of being treated as propagation delay");
        Ensure(client.State == SharpLinkConnectionState.Ready && transport.ConnectCount == 1,
            "a failed identity observation must leave transport ownership and connectivity unchanged");
    }

    [Test]
    public async Task LaterWrongHashShouldTerminateAMissingContractWait()
    {
        var transport = new TestClientTransportFactory(contractManifest: []);
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.ConnectAsync();
        var waiter = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash).AsTask();

        await PublishAndFenceAsync(transport.Connection, 1, [new(OrdersContractId, WrongHash)]);
        var failure = await CaptureExceptionAsync(waiter.WaitAsync(RaceCoordinationTimeout));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "a new manifest with the wrong hash must wake the waiter with a permanent identity failure");
    }

    [Test]
    public async Task CancelingOneContractWaitShouldLeavePeerWaitAndConnectionActive()
    {
        var transport = new TestClientTransportFactory(contractManifest: []);
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var canceled = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash, cancellation.Token).AsTask();
        var surviving = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash).AsTask();

        cancellation.Cancel();
        var failure = await CaptureExceptionAsync(canceled.WaitAsync(RaceCoordinationTimeout));
        Ensure(failure is OperationCanceledException canceledException &&
               canceledException.CancellationToken == cancellation.Token,
            "caller cancellation must retain its token and cancel only that observation");
        Ensure(!surviving.IsCompleted && client.State == SharpLinkConnectionState.Ready,
            "a peer observation and Ready connection must survive cancellation");
        await PublishAndFenceAsync(transport.Connection, 1, [new(OrdersContractId, MatchingHash)]);
        await surviving.WaitAsync(RaceCoordinationTimeout);
    }

    [Test]
    public async Task StoppingShouldWakeAMissingContractWaitWithConnectionClosed()
    {
        var transport = new TestClientTransportFactory(contractManifest: []);
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.ConnectAsync();
        var waiter = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash).AsTask();
        Ensure(!waiter.IsCompleted, "missing identity must leave an active observation to terminate");

        await client.StopAsync();
        var failure = await CaptureExceptionAsync(waiter.WaitAsync(RaceCoordinationTimeout));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "Stop must terminate remote identity observations using the connection-closed taxonomy");
    }

    [Test]
    public async Task OlderManifestShouldNotRestoreAnUnregisteredContract()
    {
        var transport = new TestClientTransportFactory();
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.ConnectAsync();
        await PublishAndFenceAsync(transport.Connection, 2, []);
        var waiter = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash).AsTask();
        Ensure(!waiter.IsCompleted, "an unregister manifest must affect new observations immediately");

        await PublishAndFenceAsync(transport.Connection, 1, [new(OrdersContractId, MatchingHash)]);
        Ensure(!waiter.IsCompleted, "an older generation must not restore an unregistered identity");
        await PublishAndFenceAsync(transport.Connection, 3, [new(OrdersContractId, MatchingHash)]);
        await waiter.WaitAsync(RaceCoordinationTimeout);
    }

    [Test]
    public async Task ReadinessShouldRequireEveryCurrentlyReadyEndpointToAdvertiseExactIdentity()
    {
        var matching = new TestClientTransportFactory();
        var missing = new TestClientTransportFactory(contractManifest: []);
        await using var client = CreateBuilder()
            .UseEndpoints([Endpoint("matching", 5001), Endpoint("missing", 5002)],
                endpoint => endpoint.Id == "matching" ? matching : missing)
            .Build();
        await client.WaitForReadinessAsync(2).AsTask().WaitAsync(RaceCoordinationTimeout);

        var waiter = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash).AsTask();
        Ensure(!waiter.IsCompleted, "one matching endpoint must not hide another Ready endpoint's missing identity");
        await PublishAndFenceAsync(missing.Connection, 1, [new(OrdersContractId, MatchingHash)]);
        await waiter.WaitAsync(RaceCoordinationTimeout);
        Ensure(client.Get<IOrdersContract>() is OrdersProxy,
            "all Ready endpoint identities must agree before synchronous acquisition is accepted");
    }

    [Test]
    public async Task MissingEndpointShouldNotHideAnotherReadyEndpointsWrongHash()
    {
        var missing = new TestClientTransportFactory(contractManifest: []);
        var wrong = new TestClientTransportFactory(contractManifest: [new(OrdersContractId, WrongHash)]);
        await using var client = CreateBuilder()
            .UseEndpoints([Endpoint("missing", 5001), Endpoint("wrong", 5002)],
                endpoint => endpoint.Id == "missing" ? missing : wrong)
            .Build();
        await client.WaitForReadinessAsync(2).AsTask().WaitAsync(RaceCoordinationTimeout);

        var failure = await CaptureExceptionAsync(client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash)
            .AsTask().WaitAsync(RaceCoordinationTimeout));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "a missing identity must not turn another Ready endpoint's incompatibility into an indefinite wait");
    }

    [Test]
    public async Task ACompletedObservationShouldNotPromiseLaterGetSurvivesUnregister()
    {
        var transport = new TestClientTransportFactory();
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash);
        await PublishAndFenceAsync(transport.Connection, 1, []);

        Exception? failure = null;
        try { _ = client.Get<IOrdersContract>(); }
        catch (SharpLinkException exception) { failure = exception; }

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "readiness is an observation rather than an identity lease; later Get must still reject unregister");
    }

    [Test]
    public async Task ReconnectShouldWakePendingObservationAndUseNewSessionsInitialGeneration()
    {
        await using var transport = new ReconnectingTransportFactory();
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.ConnectAsync();
        await PublishAndFenceAsync(transport.First, 9, []);
        var waiter = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash).AsTask();

        await transport.First.DisposeAsync();
        await transport.ReconnectStarted.Task.WaitAsync(RaceCoordinationTimeout);
        Ensure(!waiter.IsCompleted, "a disconnected missing session must not complete the pending observation");
        transport.ReleaseReconnect();
        await waiter.WaitAsync(RaceCoordinationTimeout);

        Ensure(transport.ConnectCount == 2 && client.State == SharpLinkConnectionState.Ready,
            "the same observation must survive reconnect and accept the new session's generation zero manifest");
        Ensure(client.Get<IOrdersContract>() is OrdersProxy,
            "retired session generation nine must not suppress the new session's matching initial manifest");
    }

    [Test]
    public async Task EmptyHashAndEntryCancellationShouldFailBeforeStartingConnectivity()
    {
        var transport = new TestClientTransportFactory();
        await using var client = CreateBuilder().UseTransport(transport).Build();
        var emptyFailure = await CaptureExceptionAsync(Task.Run(async () =>
            await client.WaitForRemoteContractAsync(OrdersContractId, default)));
        var zeroIdFailure = await CaptureExceptionAsync(Task.Run(async () =>
            await client.WaitForRemoteContractAsync(0, MatchingHash)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledFailure = await CaptureExceptionAsync(Task.Run(async () =>
            await client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash, cancellation.Token)));

        Ensure(emptyFailure is ArgumentException { ParamName: "rpcAssemblyHash" },
            "an exact nonempty identity must be required before any connectivity work");
        Ensure(zeroIdFailure is ArgumentOutOfRangeException { ParamName: "contractId" },
            "a zero contract ID cannot exist in a manifest and must be rejected at entry");
        Ensure(canceledFailure is OperationCanceledException && transport.ConnectCount == 0,
            "entry cancellation must be observed without starting the client");
    }

    [Test]
    public async Task ExplicitPreviousHashShouldRemainPendingUntilTargetHashArrives()
    {
        var transport = new TestClientTransportFactory(contractManifest: [new(OrdersContractId, WrongHash)]);
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.ConnectAsync();

        var waiter = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash, WrongHash).AsTask();
        Ensure(!waiter.IsCompleted, "the explicitly identified previous hash represents pending replacement propagation");
        await PublishAndFenceAsync(transport.Connection, 1, [new(OrdersContractId, MatchingHash)]);
        await waiter.WaitAsync(RaceCoordinationTimeout);

        Ensure(client.Get<IOrdersContract>() is OrdersProxy && transport.ConnectCount == 1,
            "the target hash must unblock replacement observation on the original connection");
    }

    [Test]
    public async Task ExplicitPreviousHashShouldNotPermitAnUnrelatedThirdHash()
    {
        var transport = new TestClientTransportFactory(contractManifest: [new(OrdersContractId, WrongHash)]);
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.ConnectAsync();
        var waiter = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash, WrongHash).AsTask();

        await PublishAndFenceAsync(transport.Connection, 1, [new(OrdersContractId, new RpcHash128(3, 4))]);
        var failure = await CaptureExceptionAsync(waiter.WaitAsync(RaceCoordinationTimeout));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "only the explicitly known previous identity may be pending; a third identity must fail closed");
    }

    [Test]
    public async Task SamePreviousAndTargetHashShouldCompleteWithoutWaitingForAnotherGeneration()
    {
        var transport = new TestClientTransportFactory();
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.ConnectAsync();

        var waiter = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash, MatchingHash);

        Ensure(waiter.IsCompletedSuccessfully,
            "the barrier observes exact wire identity and must not imply a registry replacement generation acknowledgement");
        await waiter;
    }

    [Test]
    public async Task ReplacementCancellationShouldLeaveAnotherReplacementObservationActive()
    {
        var transport = new TestClientTransportFactory(contractManifest: [new(OrdersContractId, WrongHash)]);
        await using var client = CreateBuilder().UseTransport(transport).Build();
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var canceled = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash, WrongHash, cancellation.Token).AsTask();
        var surviving = client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash, WrongHash).AsTask();

        cancellation.Cancel();
        var failure = await CaptureExceptionAsync(canceled.WaitAsync(RaceCoordinationTimeout));
        Ensure(failure is OperationCanceledException canceledException &&
               canceledException.CancellationToken == cancellation.Token && !surviving.IsCompleted,
            "replacement cancellation must remain private to its caller rather than release or cancel peer observations");
        await PublishAndFenceAsync(transport.Connection, 1, [new(OrdersContractId, MatchingHash)]);
        await surviving.WaitAsync(RaceCoordinationTimeout);
    }

    [Test]
    public async Task ReplacementEntryValidationShouldRejectZeroIdAndEmptyHashesBeforeConnectivity()
    {
        var transport = new TestClientTransportFactory();
        await using var client = CreateBuilder().UseTransport(transport).Build();
        var zeroIdFailure = await CaptureExceptionAsync(Task.Run(async () =>
            await client.WaitForRemoteContractAsync(0, MatchingHash, WrongHash)));
        var emptyTargetFailure = await CaptureExceptionAsync(Task.Run(async () =>
            await client.WaitForRemoteContractAsync(OrdersContractId, default(RpcHash128), WrongHash)));
        var emptyPreviousFailure = await CaptureExceptionAsync(Task.Run(async () =>
            await client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash, default(RpcHash128))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledFailure = await CaptureExceptionAsync(Task.Run(async () =>
            await client.WaitForRemoteContractAsync(OrdersContractId, MatchingHash, WrongHash, cancellation.Token)));

        Ensure(zeroIdFailure is ArgumentOutOfRangeException { ParamName: "contractId" } &&
               emptyTargetFailure is ArgumentException { ParamName: "rpcAssemblyHash" } &&
               emptyPreviousFailure is ArgumentException { ParamName: "previousRpcAssemblyHash" },
            "replacement must reject impossible IDs and require both exact nonempty hashes");
        Ensure(canceledFailure is OperationCanceledException && transport.ConnectCount == 0,
            "replacement entry cancellation must not start connectivity");
    }

    [Test]
    public async Task ScopedReplacementObservationShouldForwardExplicitPreviousHashToSelectedChild()
    {
        var transport = new TestClientTransportFactory(contractManifest: [new(OrdersContractId, WrongHash)]);
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(transport))
            .Build();
        await client.StartAsync();
        await client.WaitForReadyAsync("orders").AsTask().WaitAsync(RaceCoordinationTimeout);

        var waiter = client.WaitForRemoteContractAsync("orders", OrdersContractId, MatchingHash, WrongHash).AsTask();
        Ensure(!waiter.IsCompleted, "the selected child's known previous identity must remain pending");
        await PublishAndFenceAsync(transport.Connection, 1, [new(OrdersContractId, MatchingHash)]);
        await waiter.WaitAsync(RaceCoordinationTimeout);
        Ensure(client.Get<IOrdersContract>() is OrdersProxy,
            "the scoped replacement barrier must preserve existing route and acquisition semantics");
    }

    [Test]
    public async Task ScopedClusterObservationShouldIgnoreAnotherClustersBlockedConnectivity()
    {
        var blocked = new BlockingTransportFactory();
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .AddCluster("unrelated", child => child.UseTransport(blocked),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.StartAsync();
        await blocked.ConnectStarted.Task.WaitAsync(RaceCoordinationTimeout);

        await client.WaitForRemoteContractAsync("orders", OrdersContractId, MatchingHash)
            .AsTask().WaitAsync(RaceCoordinationTimeout);

        Ensure(client.GetClusterState("unrelated") != SharpLinkConnectionState.Ready,
            "a selected cluster's exact identity must not depend on unrelated cluster connectivity");
        Ensure(client.Get<IOrdersContract>() is OrdersProxy,
            "the unchanged route must acquire the matching selected child's proxy");
    }

    [Test]
    public async Task ClusterRemovalShouldEndOldObservationBeforeSameKeyIsAddedAgain()
    {
        var missing = new TestClientTransportFactory(contractManifest: []);
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(missing))
            .Build();
        await client.StartAsync();
        await client.WaitForReadyAsync("orders").AsTask().WaitAsync(RaceCoordinationTimeout);
        var oldWait = client.WaitForRemoteContractAsync("orders", OrdersContractId, MatchingHash).AsTask();

        var removal = await client.RemoveClusterAsync("orders", RaceCoordinationTimeout);
        Ensure(removal.Succeeded && removal.ReferencesReleased, "the selected child must finish retirement");
        var addition = await AddClusterWithFixedDiscoveryAsync(client, "orders",
            child => child.UseTransport(new TestClientTransportFactory()));
        Ensure(addition.Succeeded, "the same cluster key must support a distinct child lifetime");
        await client.WaitForRemoteContractAsync("orders", OrdersContractId, MatchingHash)
            .AsTask().WaitAsync(RaceCoordinationTimeout);
        var oldFailure = await CaptureExceptionAsync(oldWait.WaitAsync(RaceCoordinationTimeout));

        Ensure(oldFailure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "a pending scoped observation must terminate with its removed child rather than migrate to a new slot");
    }

    private static SharpClientBuilder CreateBuilder()
        => SharpClientBuilder.Create()
            .UseGeneratedManifestSource(new FixedGeneratedManifestSource([Manifest.Instance]))
            .DisableRequestTimeout();

    private static async Task PublishAndFenceAsync(
        TestTransportConnection connection,
        long generation,
        KeyValuePair<long, RpcHash128>[] contracts)
    {
        using var payload = new PooledByteBufferWriter();
        ProtocolV2ContractManifestCodec.Write(payload,
            new ProtocolV2ContractManifest(generation, contracts), new SharpLinkProtocolOptions());
        await connection.InjectFrameAsync(ProtocolV2FrameType.ContractManifest,
            ProtocolV2FrameFlags.None, 0, payload.WrittenMemory);
        var timestamp = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(timestamp, ObservationContractId);
        await connection.InjectFrameAsync(ProtocolV2FrameType.Ping,
            ProtocolV2FrameFlags.None, 0, timestamp);
        var pong = await connection.WaitForSentFrame(ProtocolV2FrameType.Pong).WaitAsync(RaceCoordinationTimeout);
        Ensure(BinaryPrimitives.ReadInt64LittleEndian(pong.Payload) == ObservationContractId,
            "the ordered Pong proves the preceding manifest was processed without polling readiness state");
    }

    private sealed class ReconnectingTransportFactory : IClientTransportFactory
    {
        private readonly TestTransportConnection _second = new();
        private readonly TaskCompletionSource _reconnectRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;
        internal TestTransportConnection First { get; } = new();
        internal TaskCompletionSource ReconnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _connectCount) == 1)
            {
                await First.InjectSuccessfulHandshakeAsync(contractManifest: [], cancellationToken: cancellationToken);
                return First;
            }
            ReconnectStarted.TrySetResult();
            await _reconnectRelease.Task.WaitAsync(cancellationToken);
            await _second.InjectSuccessfulHandshakeAsync(
                contractManifest: [new KeyValuePair<long, RpcHash128>(OrdersContractId, MatchingHash)],
                cancellationToken: cancellationToken);
            return _second;
        }

        internal void ReleaseReconnect() => _reconnectRelease.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            _reconnectRelease.TrySetResult();
            await First.DisposeAsync();
            await _second.DisposeAsync();
        }
    }
}
