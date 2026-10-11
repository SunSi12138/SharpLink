using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientContractAdmissionRaceTests : ContractAdmissionTestSupport
{
    [Test]
    public async Task PolicyRejectionMustNotBecomeCompatibilityWaitWhenItsCallbackWithdrawsContract()
    {
        var transport = Transport(Manifest.Instance.RpcAssemblyHash);
        var policy = new RejectAfterPublicationPolicy(() =>
            PublishManifest(transport.Connection, 1, new RpcHash128(1, 2)).GetAwaiter().GetResult());
        await using var client = Builder().UseEndpoint(Endpoint("policy-race", 7801), _ => transport)
            .UseEndpointAdmission(policy).Build();
        await client.ConnectAsync();
        var proxy = (OrdersProxy)client.CreateProxy<IOrdersContract>(new RpcProxyOptions { Availability = RpcAvailability.WaitForCompatible });
        var codec = new CountingRequestCodec();
        var failure = await CaptureExceptionAsync(Invoke(proxy, codec).WaitAsync(RaceCoordinationTimeout));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Unavailable } && failure is not SharpLinkContractAdmissionException,
            "policy rejection must retain its cause even when the later diagnostic snapshot is incompatible");
        Ensure(policy.Count == 1 && ((SharpLinkClient)client).ContractWaiterCountForTesting == 0,
            "snapshot classification must not turn an endpoint-policy rejection into a compatibility waiter");
        Ensure(client.GetContractReadiness(new ContractRequirement(OrdersContractId, Manifest.Instance.RpcAssemblyHash)).CompatibleEndpoints == 0,
            "the callback must publish withdrawal before policy rejection is observed");
        Ensure(codec.SerializeCount == 0 && transport.Connection.SentRequestCount == 0, "policy failure must not send business work");
    }

    [Test]
    public async Task DeadlineAtAdmissionEntryMustWinBeforeWaiterReservation()
    {
        var time = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseTransport(transport).UseTimeProvider(time)
            .UseRequestTimeout(TimeSpan.FromSeconds(5)).AddInterceptor(new ExpireBeforeNextInterceptor(time)).Build();
        await client.ConnectAsync();
        var proxy = (OrdersProxy)client.CreateProxy<IOrdersContract>(new RpcProxyOptions { Availability = RpcAvailability.WaitForCompatible });
        var codec = new CountingRequestCodec();
        var failure = await CaptureExceptionAsync(Invoke(proxy, codec).WaitAsync(RaceCoordinationTimeout));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "elapsed original budget must win at admission entry");
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == 0 && codec.SerializeCount == 0 && transport.Connection.SentRequestCount == 0,
            "already expired admission must not reserve a waiter or business payload");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InitialConnectionFailureMustExitBothAdmissionAndExplicitWait(bool explicitWait)
    {
        var expected = new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "controlled initial handshake failure");
        var transport = new FailingConnectTransport(expected);
        await using var client = Builder().UseTransport(transport).Build();
        var codec = new CountingRequestCodec();
        Task operation = explicitWait
            ? client.WaitForContractAsync(new ContractRequirement(OrdersContractId, Manifest.Instance.RpcAssemblyHash)).AsTask()
            : Invoke((OrdersProxy)client.CreateProxy<IOrdersContract>(new RpcProxyOptions { Availability = RpcAvailability.WaitForCompatible }), codec);
        var failure = await CaptureExceptionAsync(operation.WaitAsync(RaceCoordinationTimeout));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation },
            "a failed initial connection must terminate its caller rather than wait for another publication");
        Ensure(transport.Count == 1 && ((SharpLinkClient)client).ContractWaiterCountForTesting == 0 && codec.SerializeCount == 0,
            "failed initial connection must release the waiting caller without serialization or independent redial");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetirementDuringEndpointPolicyMustReleaseTheActualReservation(bool initiallyWaiting)
    {
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = initiallyWaiting ? new TestClientTransportFactory(contractManifest: []) : Transport(Manifest.Instance.RpcAssemblyHash);
        var absent = new TestClientTransportFactory(contractManifest: []);
        var policy = new RetireDuringAcquirePolicy(retirement);
        await using var client = Builder().UseEndpoints([Endpoint("retire", 7901), Endpoint("absent", 7902)],
            endpoint => endpoint.Id == "retire" ? first : absent).UseEndpointAdmission(policy).Build();
        await client.WaitForReadinessAsync(2).AsTask().WaitAsync(RaceCoordinationTimeout);
        var runtime = (SharpLinkClient)client;
        ClientConnection? reserved = null;
        runtime._callAdmissionReservedTestHook = connection => reserved = connection;
        var requirement = new ContractRequirement(OrdersContractId, Manifest.Instance.RpcAssemblyHash)
        { Availability = RpcAvailability.WaitForCompatible, LocalRetirement = retirement.Task };
        var channel = new SharpLinkContractRpcChannel(runtime, requirement);
        var codec = new CountingRequestCodec();
        try
        {
            var call = channel.InvokeUnaryAsync(Method, 42, codec, channel.RuntimeContext.Codecs.GetCodec<int>(), null).AsTask();
            if (initiallyWaiting)
            {
                Ensure(!call.IsCompleted && runtime.ContractWaiterCountForTesting == 1 && policy.Count == 0,
                    "an incompatible topology must wait before endpoint policy acquires anything");
                await PublishManifest(first.Connection, 1, Manifest.Instance.RpcAssemblyHash);
            }
            var failure = await CaptureExceptionAsync(call.WaitAsync(RaceCoordinationTimeout));
            Ensure(failure is SharpLinkContractAdmissionException { Code: SharpLinkErrorCode.Unavailable },
                "local retirement between admission precheck and ownership transfer must terminate the call");
            Ensure(policy.Count == 1 && retirement.Task.IsCompleted && reserved is not null,
                "the policy must retire the local requirement before the physical reservation returns");
            Ensure(reserved!.CallAdmissionReservationCount == 0 && runtime.PendingCallCount == 0 &&
                   runtime.ActiveClientCallCount == 0 && runtime.ContractWaiterCountForTesting == 0,
                "both fast and resumed admission must release the exact reservation and waiter");
            Ensure(codec.SerializeCount == 0 && first.Connection.SentRequestCount == 0 && absent.Connection.SentRequestCount == 0,
                "retirement must win before payload serialization or Request output");
        }
        finally { runtime._callAdmissionReservedTestHook = null; }
    }

    private sealed class RetireDuringAcquirePolicy(TaskCompletionSource retirement) : ISharpLinkEndpointAdmissionPolicy
    {
        internal int Count;
        public SharpLinkEndpointAdmissionDecision TryAcquire(in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
        {
            Interlocked.Increment(ref Count);
            retirement.TrySetResult();
            return new SharpLinkEndpointAdmissionDecision(true, 1, null);
        }
        public void Report(in SharpLinkEndpointOutcome outcome, long reportToken) { }
    }

    private sealed class RejectAfterPublicationPolicy(Action publish) : ISharpLinkEndpointAdmissionPolicy
    {
        internal int Count;
        public SharpLinkEndpointAdmissionDecision TryAcquire(in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
        {
            Interlocked.Increment(ref Count);
            publish();
            return new SharpLinkEndpointAdmissionDecision(false, 0, null);
        }
        public void Report(in SharpLinkEndpointOutcome outcome, long reportToken) { }
    }

    private sealed class ExpireBeforeNextInterceptor(ManualTimeProvider time) : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            time.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
            return next(context);
        }
    }

    private sealed class FailingConnectTransport(Exception failure) : IClientTransportFactory
    {
        internal int Count;
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return ValueTask.FromException<ITransportConnection>(failure);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
