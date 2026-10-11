using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientContractAdmissionTests : ContractAdmissionTestSupport
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IncompatibleCallShouldFailBeforeBusinessSerialization(bool absent)
    {
        var transport = absent ? new TestClientTransportFactory(contractManifest: []) : Transport(new RpcHash128(1, 2));
        await using var client = Builder().UseTransport(transport).Build();
        var proxy = (OrdersProxy)client.Get<IOrdersContract>();
        await client.ConnectAsync();
        var codec = new CountingRequestCodec();
        var failure = await CaptureExceptionAsync(Invoke(proxy, codec).WaitAsync(RaceCoordinationTimeout));
        Ensure(failure is SharpLinkContractAdmissionException { Code: SharpLinkErrorCode.Unavailable },
            "an incompatible call must fail with a structured availability error");
        Ensure(((SharpLinkContractAdmissionException)failure!).Snapshot.State ==
               (absent ? ContractObservationState.Absent : ContractObservationState.ObservedIncompatible),
            "the observation must distinguish missing contracts from a hash mismatch");
        Ensure(codec.SerializeCount == 0 && transport.Connection.SentRequestCount == 0,
            "incompatible calls must fail before business serialization and Request output");
        Ensure(client.State == SharpLinkConnectionState.Ready, "incompatibility must not disconnect a ready session");
    }

    [Test]
    public async Task MixedEndpointsShouldOnlySendToCompatibleSession()
    {
        var matching = Transport(Manifest.Instance.RpcAssemblyHash);
        var mismatching = Transport(new RpcHash128(3, 4));
        var absent = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseEndpoints(
            [Endpoint("match", 7101), Endpoint("mismatch", 7102), Endpoint("absent", 7103)],
            endpoint => endpoint.Id switch { "match" => matching, "mismatch" => mismatching, _ => absent })
            .UseCluster(static options => options.MinReadyEndpoints = 3).Build();
        await client.WaitForReadinessAsync(3).AsTask().WaitAsync(RaceCoordinationTimeout);
        var proxy = (OrdersProxy)client.Get<IOrdersContract>();
        var codec = new CountingRequestCodec();
        for (var index = 0; index < 8; index++)
            await CompleteCall(proxy, codec, matching.Connection);
        Ensure(codec.SerializeCount == 8 && matching.Connection.SentRequestCount == 8 &&
               mismatching.Connection.SentRequestCount == 0 && absent.Connection.SentRequestCount == 0,
            "every invocation must use only the compatible physical session");
    }

    [Test]
    public async Task AllIncompatibleEndpointsShouldFailWithoutSerializingOrSending()
    {
        var mismatching = Transport(new RpcHash128(3, 4));
        var absent = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseEndpoints([Endpoint("mismatch", 7201), Endpoint("absent", 7202)],
            endpoint => endpoint.Id == "mismatch" ? mismatching : absent).Build();
        await client.WaitForReadinessAsync(2).AsTask().WaitAsync(RaceCoordinationTimeout);
        var codec = new CountingRequestCodec();
        var failure = await CaptureExceptionAsync(Invoke((OrdersProxy)client.Get<IOrdersContract>(), codec).WaitAsync(RaceCoordinationTimeout));
        Ensure(failure is SharpLinkContractAdmissionException { Code: SharpLinkErrorCode.Unavailable }, "incompatible topology must fail");
        Ensure(codec.SerializeCount == 0 && mismatching.Connection.SentRequestCount == 0 && absent.Connection.SentRequestCount == 0,
            "no incompatible endpoint may receive business work");
    }

    [Test]
    public async Task HeldProxyShouldReadmitAgainstEachPublishedManifestGeneration()
    {
        var transport = Transport(Manifest.Instance.RpcAssemblyHash);
        await using var client = Builder().UseTransport(transport).Build();
        await client.ConnectAsync();
        var held = (OrdersProxy)client.Get<IOrdersContract>();
        var codec = new CountingRequestCodec();
        await CompleteCall(held, codec, transport.Connection);
        await PublishManifest(transport.Connection, 1, new RpcHash128(5, 6));
        var failure = await CaptureExceptionAsync(Invoke(held, codec).WaitAsync(RaceCoordinationTimeout));
        Ensure(failure is SharpLinkContractAdmissionException { Code: SharpLinkErrorCode.Unavailable }, "held proxy must readmit each call");
        Ensure(codec.SerializeCount == 1 && transport.Connection.SentRequestCount == 1, "mismatch must not serialize or send");
        Ensure(client.Get<IOrdersContract>() is OrdersProxy, "Get remains local after manifest publication");
        await PublishManifest(transport.Connection, 2, Manifest.Instance.RpcAssemblyHash);
        await CompleteCall(held, codec, transport.Connection);
        Ensure(codec.SerializeCount == 2 && transport.Connection.SentRequestCount == 2, "held proxy resumes after hash restoration");
    }
    [Test]
    public async Task WaitPolicyShouldResumeOnlyAfterCompatiblePublication()
    {
        var transport = Transport(new RpcHash128(9, 10));
        var absent = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseEndpoints([Endpoint("mismatch", 7401), Endpoint("absent", 7402)],
            endpoint => endpoint.Id == "mismatch" ? transport : absent).Build();
        await client.WaitForReadinessAsync(2).AsTask().WaitAsync(RaceCoordinationTimeout);
        var proxy = (OrdersProxy)client.CreateProxy<IOrdersContract>(
            new RpcProxyOptions { Availability = RpcAvailability.WaitForCompatible });
        var codec = new CountingRequestCodec();
        var call = Invoke(proxy, codec);
        Ensure(!call.IsCompleted && ((SharpLinkClient)client).ContractWaiterCountForTesting == 1,
            "WaitForCompatible must register a pre-send waiter for the same logical call");
        Ensure(codec.SerializeCount == 0 && transport.Connection.SentRequestCount == 0,
            "waiting must own no serialized business payload or Request frame");

        await PublishManifest(transport.Connection, 1, Manifest.Instance.RpcAssemblyHash, synchronize: false);
        var request = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request)
            .WaitAsync(RaceCoordinationTimeout);
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)request.Header.RequestId), 7);
        Ensure(await call.WaitAsync(RaceCoordinationTimeout) == 7 && codec.SerializeCount == 1,
            "publication must resume the original invocation exactly once");
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == 0,
            "successful admission must remove its waiter");
    }

    [Test]
    public async Task CanceledWaitShouldRemoveWaiterWithoutSending()
    {
        var transport = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseTransport(transport).Build();
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var proxy = (OrdersProxy)client.CreateProxy<IOrdersContract>(
            new RpcProxyOptions { Availability = RpcAvailability.WaitForCompatible });
        var codec = new CountingRequestCodec();
        var call = Invoke(proxy, codec, cancellation.Token);
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == 1, "call must enter waiting");

        cancellation.Cancel();
        var failure = await CaptureExceptionAsync(call.WaitAsync(RaceCoordinationTimeout));
        Ensure(failure is OperationCanceledException or SharpLinkException { Code: SharpLinkErrorCode.Cancelled },
            "caller cancellation must terminate the pre-send wait");
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == 0 && codec.SerializeCount == 0 &&
               transport.Connection.SentRequestCount == 0, "cancellation must clean up without business work");
    }

    [Test]
    public async Task StopShouldTerminateWaitWithNoPayloadOrLeakedWaiter()
    {
        var transport = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseTransport(transport).Build();
        await client.ConnectAsync();
        var proxy = (OrdersProxy)client.CreateProxy<IOrdersContract>(
            new RpcProxyOptions { Availability = RpcAvailability.WaitForCompatible });
        var codec = new CountingRequestCodec();
        var call = Invoke(proxy, codec);
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == 1, "call must enter waiting");

        await client.StopAsync().AsTask().WaitAsync(RaceCoordinationTimeout);
        var failure = await CaptureExceptionAsync(call.WaitAsync(RaceCoordinationTimeout));
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "stop must terminate pending compatibility waits");
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == 0 && codec.SerializeCount == 0 &&
               transport.Connection.SentRequestCount == 0, "stop must release the waiter without business work");
    }

    [Test]
    public async Task WaitShouldConsumeTheOriginalLogicalCallDeadline()
    {
        var time = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseTimeProvider(time)
            .UseRequestTimeout(TimeSpan.FromSeconds(5)).UseTransport(transport).Build();
        await client.ConnectAsync();
        var proxy = (OrdersProxy)client.CreateProxy<IOrdersContract>(
            new RpcProxyOptions { Availability = RpcAvailability.WaitForCompatible });
        var codec = new CountingRequestCodec();
        var call = Invoke(proxy, codec);
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == 1, "call must enter waiting");

        time.Advance(TimeSpan.FromSeconds(5));
        var failure = await CaptureExceptionAsync(call.WaitAsync(RaceCoordinationTimeout));
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "the original request deadline must bound compatibility waiting");
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == 0 && codec.SerializeCount == 0 &&
               transport.Connection.SentRequestCount == 0, "deadline must release the waiter before serialization");
        var runtime = (SharpLinkClient)client;
        Ensure(runtime.PendingCallCount == 0 && runtime.ActiveClientCallCount == 0 && runtime.ActiveClientStreamCount == 0,
            "an expired pre-send wait must not leak a pending call or reserved connection/stream lease");
    }

    [Test]
    public async Task ExplicitReadinessShouldWaitForMinimumDistinctCompatibleEndpoints()
    {
        var first = Transport(Manifest.Instance.RpcAssemblyHash);
        var second = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseEndpoints(
            [Endpoint("first", 7301), Endpoint("second", 7302)],
            endpoint => endpoint.Id == "first" ? first : second).Build();
        await client.WaitForReadinessAsync(2).AsTask().WaitAsync(RaceCoordinationTimeout);
        var requirement = new ContractRequirement(OrdersContractId, Manifest.Instance.RpcAssemblyHash);
        Ensure(client.GetContractReadiness(requirement).CompatibleEndpoints == 1,
            "readiness must count only matching endpoints");
        var waiting = client.WaitForContractAsync(requirement,
            new ContractWaitOptions { MinimumCompatibleEndpoints = 2 }).AsTask();
        Ensure(!waiting.IsCompleted && ((SharpLinkClient)client).ContractWaiterCountForTesting == 1,
            "one matching endpoint cannot satisfy an explicit minimum of two");

        await PublishManifest(second.Connection, 1, Manifest.Instance.RpcAssemblyHash);
        var snapshot = await waiting.WaitAsync(RaceCoordinationTimeout);
        Ensure(snapshot.CompatibleEndpoints == 2 && snapshot.Endpoints.Count == 2,
            "publication must satisfy the requested distinct endpoint minimum");
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == 0 &&
               first.Connection.SentRequestCount == 0 && second.Connection.SentRequestCount == 0,
            "explicit readiness must not invoke business work or retain a waiter");
    }

    [Test]
    public async Task WaitCapacityShouldRejectOverflowAndReleaseEveryCanceledWaiter()
    {
        var transport = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseTransport(transport).Build();
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var proxy = (OrdersProxy)client.CreateProxy<IOrdersContract>(new RpcProxyOptions { Availability = RpcAvailability.WaitForCompatible });
        var codec = new CountingRequestCodec();
        var calls = new Task<int>[1024];
        for (var index = 0; index < calls.Length; index++)
            calls[index] = Invoke(proxy, codec, cancellation.Token);
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == calls.Length, "1024 wait slots must be accepted");
        var overflow = await CaptureExceptionAsync(Invoke(proxy, codec).WaitAsync(RaceCoordinationTimeout));
        Ensure(overflow is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted }, "wait overflow must be bounded");
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == calls.Length, "overflow must not consume a slot");
        cancellation.Cancel();
        _ = await CaptureExceptionAsync(Task.WhenAll(calls).WaitAsync(RaceCoordinationTimeout));
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == 0 && codec.SerializeCount == 0 && transport.Connection.SentRequestCount == 0,
            "cancellation must release every slot without business work");
    }
}
