using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleReadinessDrainSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientLifecycleReadinessDrainTests
{
    [Test]
    public async Task ConcurrentClientConnectionDisposersShouldAwaitPhysicalCleanup()
    {
        await using var owner = ClientBuilderTestHelper.Build(new NonConnectingFactory());
        using var context = CreateRuntimeContext();
        var transport = new BlockingDisposeConnection();
        var connection = new ClientConnection(
            owner,
            new RpcSession(transport, RpcSessionTestFixture.ClientOptions(context)),
            new CancellationTokenSource(),
            8,
            context);

        var first = connection.DisposeAsync().AsTask();
        await transport.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = connection.DisposeAsync().AsTask();

        Ensure(!second.IsCompleted, "concurrent disposal must await physical transport cleanup");
        transport.ReleaseDispose();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task CancellationCallbackFailureMustNotStrandPendingCalls()
    {
        await using var owner = ClientBuilderTestHelper.Build(new NonConnectingFactory());
        using var context = CreateRuntimeContext();
        using var cancellation = new CancellationTokenSource();
        using var callback = cancellation.Token.Register(
            static () => throw new InvalidOperationException("connection cancellation callback failed"));
        var connection = new ClientConnection(
            owner,
            CreateReadySession(context),
            cancellation,
            8,
            context);
        var operation = connection.PendingCalls.Rent<int>(out _);
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "connection failed");

        try
        {
            connection.Fail(terminal);
            try
            {
                _ = await operation.AsValueTask();
                throw new Exception("expected pending call failure");
            }
            catch (SharpLinkException exception)
            {
                Ensure(ReferenceEquals(exception, terminal), "pending call must retain terminal failure");
            }
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Test]
    public async Task EndpointSelectionKernelShouldHandleEmptyAndSingleConnectionSnapshots()
    {
        Ensure(EndpointSelectionKernel.SelectConnection([]) is null, "empty connection snapshot");
        await using var owner = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        using var context = CreateRuntimeContext();
        await using var connection = new ClientConnection(
            owner,
            CreateReadySession(context),
            new CancellationTokenSource(),
            8,
            context);

        connection.Session.NotifyConnected();
        connection.Session.AssertStateInvariant();
        connection.AssertStateInvariant();
        Ensure(ReferenceEquals(EndpointSelectionKernel.SelectConnection([connection]), connection),
            "ready single connection");
        connection.MarkDraining();
        connection.Session.AssertStateInvariant();
        connection.AssertStateInvariant();
        Ensure(EndpointSelectionKernel.SelectConnection([connection]) is null,
            "draining single connection");
    }

    [Test]
    public async Task SecondHandshakeResponseShouldTerminateThePublishedSession()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        var context = (SharpLinkRuntimeContext)client.RuntimeContext;
        await client.ConnectAsync();
        var readyConnectionsField = typeof(SharpLinkClient).GetField(
            "_readyConnections",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find ready connection snapshot");
        var connection = ((ClientConnection[])readyConnectionsField.GetValue(client)!)[0];
        var disconnected = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Session.OnDisconnected += exception => disconnected.TrySetResult(exception);
        var pending = connection.PendingCalls.Rent<int>(out _);
        var payload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteHandshakeResponse(payload, new ProtocolV2HandshakeResponse(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.None,
            context.Protocol.MaxFramePayloadBytes,
            context.FlowControl.StreamReceiveWindowBytes,
            context.FlowControl.ConnectionReceiveWindowBytes));

        await transport.Connection.InjectFrameAsync(
            ProtocolV2FrameType.HandshakeResponse,
            ProtocolV2FrameFlags.None,
            0,
            payload.WrittenMemory);
        var failure = await CaptureSharpLinkExceptionAsync(
            pending.AsValueTask().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(failure.Code == SharpLinkErrorCode.ProtocolViolation,
            "a second handshake response must be a structured protocol failure");
        Ensure(connection.Session.ProtocolPhase is
                   RpcSessionProtocolPhase.Stopping or RpcSessionProtocolPhase.Terminal &&
               connection.Session.NegotiatedOptions is not null &&
               !connection.CanAcceptCalls,
            "a duplicate response must terminate the already-published snapshot and reject new calls");
    }

    [Test]
    public async Task PowerOfTwoChoiceShouldSelectLowerActiveConnection()
    {
        await using var owner = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        using var context = CreateRuntimeContext();
        await using var first = new ClientConnection(
            owner,
            CreateReadySession(context),
            new CancellationTokenSource(),
            8,
            context);
        await using var second = new ClientConnection(
            owner,
            CreateReadySession(context),
            new CancellationTokenSource(),
            8,
            context);
        var firstCall1 = first.PendingCalls.Rent<int>(out var firstId1);
        var firstCall2 = first.PendingCalls.Rent<int>(out var firstId2);
        var secondCall = second.PendingCalls.Rent<int>(out var secondId);

        var selected = EndpointSelectionKernel.SelectConnection([first, second]);
        Ensure(ReferenceEquals(selected, second), "power-of-two should select the lower active count");

        var completed = new InvalidOperationException("test completion");
        first.PendingCalls.DispatchError(firstId1, completed);
        first.PendingCalls.DispatchError(firstId2, completed);
        second.PendingCalls.DispatchError(secondId, completed);
        await ObserveFailureAsync(firstCall1.AsValueTask());
        await ObserveFailureAsync(firstCall2.AsValueTask());
        await ObserveFailureAsync(secondCall.AsValueTask());
    }

    [Test]
    public async Task ClusterSelectionShouldFallBackFromAStalePooledConnection()
    {
        await using var owner = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        using var context = CreateRuntimeContext();
        await using var stale = new ClientConnection(
            owner,
            CreateReadySession(context),
            new CancellationTokenSource(),
            8,
            context);
        await using var ready = new ClientConnection(
            owner,
            CreateReadySession(context),
            new CancellationTokenSource(),
            8,
            context);
        stale.Session.NotifyConnected();
        ready.Session.NotifyConnected();
        Ensure(ready.TryBeginUntrackedCall(), "ready connection active-call setup");
        stale.MarkDraining();

        try
        {
            Ensure(ReferenceEquals(
                    EndpointSelectionKernel.SelectConnection([stale, ready]),
                    ready),
                "shared cluster selection should fall back to an accepting pooled connection");
        }
        finally
        {
            ready.EndUntrackedCall();
        }
    }

    [Test]
    public async Task AdmissionRetryAfterShouldSurviveAStaleGrantedConnection()
    {
        var policy = new AdmitFirstRejectSecondPolicy(TimeSpan.FromMilliseconds(100));
        await using var client = ClientBuilderTestHelper.Build(
            new TestClientTransportFactory(),
            builder => builder.UseEndpointAdmission(policy));
        var stateType = typeof(SharpLinkClient).GetNestedType("AttemptOutcomeState", BindingFlags.NonPublic)
            ?? throw new Exception("cannot find attempt outcome state");
        var method = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null);
        var state = Activator.CreateInstance(
            stateType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [client, method],
            culture: null)
            ?? throw new Exception("cannot create attempt outcome state");
        var tryAcquire = stateType.GetMethod("TryAcquire", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new Exception("cannot find attempt acquisition");
        var complete = stateType.GetMethod("CompleteWithoutPending", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new Exception("cannot find attempt completion");
        var shouldHonor = stateType.GetProperty("ShouldHonorAdmissionRetryAfter", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new Exception("cannot find retry-after predicate");
        var first = new SharpLinkEndpointCandidate(CreateEndpoint("first", 5001), 1, 0, generation: 1);
        var second = new SharpLinkEndpointCandidate(CreateEndpoint("second", 5002), 1, 0, generation: 1);

        Ensure((bool)(tryAcquire.Invoke(state, [first]) ?? false), "first endpoint should be admitted");
        complete.Invoke(
            state,
            [
                PendingCallCompletionReason.ConnectionClosed,
                new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "selected connection became stale")
            ]);
        Ensure(!(bool)(tryAcquire.Invoke(state, [second]) ?? true), "second endpoint should be rejected");
        Ensure((bool)(shouldHonor.GetValue(state) ?? false),
            "a stale admitted endpoint must not suppress the current selection retry-after");
    }

    [Test]
    public async Task GoAwayShouldDrainOnlyItsConnectionAndRefillMinimumPool()
    {
        var transport = new SequenceClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
            builder.UseConnectionPool(options =>
            {
                options.MinConnections = 2;
                options.MaxConnections = 2;
            }));
        await client.ConnectAsync();
        var drainingConnection = await transport.WaitForConnectionAsync(0);
        await InjectGoAwayAsync(drainingConnection);
        await WaitUntilAsync(() => transport.ConnectCount >= 3 && client.ReadyConnectionCount == 2);

        Ensure(client.State == SharpLinkConnectionState.Ready, "another ready connection should keep the client ready");
    }

    [Test]
    public async Task GoAwayShouldCountAsBreakerFailureWithoutAnActiveCall()
    {
        var transport = new TestClientTransportFactory();
        var endpoint = new SharpLinkEndpoint
        {
            Id = "breaker",
            Address = new SharpLinkTcpAddress("127.0.0.1", 5001)
        };
        var breaker = new SharpLinkCircuitBreaker(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(5),
            HalfOpenMaxCalls = 1
        }.CloneValidated());
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            endpoint,
            transport,
            builder => builder.UseEndpointAdmission(breaker));
        await client.ConnectAsync();

        await InjectGoAwayAsync(transport.Connection);

        var candidate = new SharpLinkEndpointCandidate(endpoint, 0, 0, generation: 0);
        var method = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null);
        await WaitUntilAsync(
            () => !breaker.TryAcquire(candidate, method).IsAllowed,
            () => "GoAway was not recorded as an endpoint infrastructure failure");
    }
}
