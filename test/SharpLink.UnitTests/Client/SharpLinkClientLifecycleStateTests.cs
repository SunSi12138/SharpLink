using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public class SharpLinkClientLifecycleStateTests
{
    [Test]
    public async Task ConcurrentConnectsShouldShareOneAttemptAndReadyLoopSet()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

        var connects = new Task[16];
        for (var index = 0; index < connects.Length; index++)
            connects[index] = client.ConnectAsync().AsTask();
        await Task.WhenAll(connects);

        Ensure(transport.ConnectCount == 1, "concurrent calls should share one transport attempt");
        Ensure(client.State == SharpLinkConnectionState.Ready, "client state should be ready");
        await client.ConnectAsync();
        Ensure(transport.ConnectCount == 1, "repeated ready connect should complete without new loops");
    }

    [Test]
    public async Task SharedFixedConnectShouldSurviveFirstWaiterCancellation()
    {
        var transport = new BlockingInitialTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();

        var cancelledWaiter = client.ConnectAsync(cancellation.Token).AsTask();
        await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var survivingWaiter = client.ConnectAsync().AsTask();
        cancellation.Cancel();

        await EnsureCancelledAsync(cancelledWaiter);
        Ensure(!survivingWaiter.IsCompleted,
            "one caller cancelling its wait must not cancel the shared client-owned connect attempt");

        transport.ReleaseConnect();
        await survivingWaiter.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.State == SharpLinkConnectionState.Ready,
            "the shared fixed connect should still publish a ready connection");
    }

    [Test]
    public async Task EndpointClusterHandshakeTimeoutsShouldRetainStructuredCause()
    {
        var staticFactories = new List<HangingHandshakeTransportFactory>();
        await using (var staticClient = SharpClientBuilder.Create()
            .UseEndpoints(
                [CreateEndpoint("first", 5001), CreateEndpoint("second", 5002)],
                _ =>
                {
                    var factory = new HangingHandshakeTransportFactory();
                    staticFactories.Add(factory);
                    return factory;
                })
            .UseProtocol(static options => options.HandshakeTimeout = TimeSpan.FromMilliseconds(20))
            .Build())
        {
            var exception = await CaptureSharpLinkExceptionAsync(staticClient.ConnectAsync().AsTask());
            Ensure(ContainsHandshakeTimeout(exception),
                "static endpoint clusters must preserve the structured handshake-timeout cause");
        }

        var dynamicFactory = new HangingHandshakeTransportFactory();
        await using var dynamicClient = SharpClientBuilder.Create()
            .UseEndpointResolver(
                new FixedSnapshotResolver(new SharpLinkEndpointSnapshot(1, [CreateEndpoint("dynamic", 5003)])),
                _ => dynamicFactory)
            .UseProtocol(static options => options.HandshakeTimeout = TimeSpan.FromMilliseconds(20))
            .Build();
        var dynamicException = await CaptureSharpLinkExceptionAsync(dynamicClient.ConnectAsync().AsTask());
        Ensure(ContainsHandshakeTimeout(dynamicException),
            "dynamic endpoint clusters must preserve the structured handshake-timeout cause");
    }

    [Test]
    public async Task StopShouldBeIdempotentAndRejectLaterConnects()
    {
        var transport = new TestClientTransportFactory();
        var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        await client.ConnectAsync();

        await Task.WhenAll(
            client.StopAsync().AsTask(),
            client.StopAsync().AsTask());
        Ensure(client.State == SharpLinkConnectionState.Stopped, "stopped state");

        try
        {
            await client.ConnectAsync();
            throw new Exception("expected connect after stop to fail");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.ConnectionClosed, "connect-after-stop error code");
        }
    }

    [Test]
    public async Task DisconnectedReadySessionShouldReconnectWithFreshConnection()
    {
        var transport = new SequenceClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        await client.ConnectAsync();
        var first = await transport.WaitForConnectionAsync(0);

        await first.DisposeAsync();
        await WaitUntilAsync(() => transport.ConnectCount >= 2 && client.State == SharpLinkConnectionState.Ready);

        var second = await transport.WaitForConnectionAsync(1);
        Ensure(!ReferenceEquals(first, second), "reconnect must own a fresh transport connection");
    }

    [Test]
    public async Task ImmediatelyDrainedReconnectShouldNotLoseTheNextReconnectSignal()
    {
        const int immediatelyDrainedReconnects = 8;
        var transport = new SequenceClientTransportFactory(immediatelyDrainedReconnects);
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        await client.ConnectAsync();
        var first = await transport.WaitForConnectionAsync(0);

        await first.DisposeAsync();
        await WaitUntilAsync(
            () => transport.ConnectCount >= immediatelyDrainedReconnects + 2 &&
                  client.State == SharpLinkConnectionState.Ready,
            () => $"reconnect stalled after {transport.ConnectCount} attempts in state {client.State} " +
                  $"with {client.ReadyConnectionCount} ready connections");

        Ensure(client.ReadyConnectionCount == 1,
            "a reconnect drained before its worker exits must schedule a replacement");
    }

    [Test]
    public async Task FailedExpansionShouldHandZeroReadyPoolToReconnectWorker()
    {
        var transport = new SequenceClientTransportFactory(failedConnectsAfterInitial: 1);
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            connectionPoolOptions: new SharpLinkConnectionPoolOptions
            {
                MinConnections = 1,
                MaxConnections = 2
            });
        await client.ConnectAsync();
        var firstConnection = await transport.WaitForConnectionAsync(0);

        var firstCall = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        _ = await firstConnection.WaitForSentPacket(ProtocolV2FrameType.Request);
        var secondCall = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        await WaitUntilAsync(() => transport.ConnectCount >= 2);

        await firstConnection.DisposeAsync();
        await ObserveConnectionFailureAsync(firstCall);
        await ObserveConnectionFailureAsync(secondCall);
        await WaitUntilAsync(
            () => transport.ConnectCount >= 3 &&
                  client.ReadyConnectionCount == 1 &&
                  client.State == SharpLinkConnectionState.Ready,
            () => $"failed expansion stranded the client after {transport.ConnectCount} attempts " +
                  $"in state {client.State} with {client.ReadyConnectionCount} ready connections");
    }

    [Test]
    public async Task ConnectShouldEstablishConfiguredMinimumPoolSize()
    {
        var transport = new SequenceClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            connectionPoolOptions: new SharpLinkConnectionPoolOptions
            {
                MinConnections = 2,
                MaxConnections = 2
            });

        await client.ConnectAsync();
        Ensure(transport.ConnectCount == 2, "minimum pool should be ready when ConnectAsync returns");
        Ensure(client.ReadyConnectionCount == 2, "ready pool size");
    }

    [Test]
    public async Task PowerOfTwoChoiceShouldSelectLowerActiveConnection()
    {
        await using var owner = new SharpLinkClient(
            new TestClientTransportFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        var context = new SharpLinkRuntimeContextBuilder().Build();
        await using var first = new ClientConnection(
            owner,
            new RpcSession(new TestTransportConnection()),
            new CancellationTokenSource(),
            8,
            context.Codecs);
        await using var second = new ClientConnection(
            owner,
            new RpcSession(new TestTransportConnection()),
            new CancellationTokenSource(),
            8,
            context.Codecs);
        var firstCall1 = first.PendingCalls.Rent<int>(out var firstId1);
        var firstCall2 = first.PendingCalls.Rent<int>(out var firstId2);
        var secondCall = second.PendingCalls.Rent<int>(out var secondId);

        var selected = SharpLinkClient.SelectLeastLoaded([first, second], 0, 1);
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
        await using var owner = new SharpLinkClient(
            new TestClientTransportFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        var context = new SharpLinkRuntimeContextBuilder().Build();
        await using var stale = new ClientConnection(
            owner,
            new RpcSession(new TestTransportConnection()),
            new CancellationTokenSource(),
            8,
            context.Codecs);
        await using var ready = new ClientConnection(
            owner,
            new RpcSession(new TestTransportConnection()),
            new CancellationTokenSource(),
            8,
            context.Codecs);
        stale.Session.NotifyConnected();
        ready.Session.NotifyConnected();
        Ensure(ready.TryBeginUntrackedCall(), "ready connection active-call setup");
        stale.MarkDraining();

        try
        {
            Ensure(ReferenceEquals(
                    SelectClusterConnection("StaticClusterRuntime", 0, stale, ready),
                    ready),
                "static cluster should fall back to an accepting pooled connection");
            Ensure(ReferenceEquals(
                    SelectClusterConnection("DynamicClusterRuntime", 0L, stale, ready),
                    ready),
                "dynamic cluster should fall back to an accepting pooled connection");
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
        await using var client = new SharpLinkClient(
            new TestClientTransportFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            endpointAdmissionPolicy: policy);
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
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            connectionPoolOptions: new SharpLinkConnectionPoolOptions
            {
                MinConnections = 2,
                MaxConnections = 2
            });
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
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            fixedEndpoint: endpoint,
            endpointAdmissionPolicy: breaker);
        await client.ConnectAsync();

        await InjectGoAwayAsync(transport.Connection);

        var candidate = new SharpLinkEndpointCandidate(endpoint, 0, 0, generation: 0);
        var method = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null);
        await WaitUntilAsync(
            () => !breaker.TryAcquire(candidate, method).IsAllowed,
            () => "GoAway was not recorded as an endpoint infrastructure failure");
    }

    private static async Task InjectGoAwayAsync(TestTransportConnection connection)
    {
        var payload = new PooledByteBufferWriter();
        var lastAccepted = payload.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(lastAccepted, 0);
        payload.Advance(sizeof(ulong));
        ProtocolV2PayloadCodec.WriteError(
            payload,
            SharpLinkErrorCode.Unavailable,
            "rolling restart",
            1024,
            out _);

        await connection.InjectFrameAsync(
            ProtocolV2FrameType.GoAway,
            ProtocolV2FrameFlags.Error,
            0,
            payload.WrittenMemory);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string>? timeoutMessage = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage?.Invoke() ?? "The expected client state was not reached.");
        }
    }

    private static async Task EnsureCancelledAsync(Task operation)
    {
        try
        {
            await operation;
            throw new Exception("expected the caller wait to be cancelled");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<SharpLinkException> CaptureSharpLinkExceptionAsync(Task operation)
    {
        try
        {
            await operation;
            throw new Exception("expected a SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static bool ContainsHandshakeTimeout(Exception exception)
    {
        if (exception is SharpLinkException { Code: SharpLinkErrorCode.Unavailable } sharpLink &&
            sharpLink.Message.Contains("handshake timed out", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (exception is AggregateException aggregate)
        {
            foreach (var innerException in aggregate.InnerExceptions)
            {
                if (ContainsHandshakeTimeout(innerException))
                    return true;
            }
            return false;
        }
        return exception.InnerException is { } inner && ContainsHandshakeTimeout(inner);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static async Task ObserveFailureAsync(ValueTask<int> operation)
    {
        try
        {
            await operation;
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task ObserveConnectionFailureAsync(Task<int> operation)
    {
        try
        {
            _ = await operation;
            throw new Exception("expected the disconnected call to fail");
        }
        catch (SharpLinkException exception) when (exception.Code is
            SharpLinkErrorCode.ConnectionClosed or SharpLinkErrorCode.Unavailable)
        {
        }
    }

    private sealed class SequenceClientTransportFactory : IClientTransportFactory
    {
        private readonly Lock _gate = new();
        private readonly List<TestTransportConnection> _connections = [];
        private readonly int _immediatelyDrainedReconnects;
        private readonly int _failedConnectsAfterInitial;
        private int _connectCount;

        internal SequenceClientTransportFactory(
            int immediatelyDrainedReconnects = 0,
            int failedConnectsAfterInitial = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(immediatelyDrainedReconnects);
            ArgumentOutOfRangeException.ThrowIfNegative(failedConnectsAfterInitial);
            _immediatelyDrainedReconnects = immediatelyDrainedReconnects;
            _failedConnectsAfterInitial = failedConnectsAfterInitial;
        }

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var connectNumber = Interlocked.Increment(ref _connectCount);
            if (connectNumber > 1 && connectNumber <= _failedConnectsAfterInitial + 1)
                throw new SocketException((int)SocketError.ConnectionRefused);

            var connection = new TestTransportConnection();
            var payload = new ArrayBufferWriter<byte>();
            ProtocolV2PayloadCodec.WriteHandshakeResponse(payload, new ProtocolV2HandshakeResponse(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                4 * 1024 * 1024,
                1024 * 1024,
                16 * 1024 * 1024));
            await connection.InjectFrameAsync(
                ProtocolV2FrameType.HandshakeResponse,
                ProtocolV2FrameFlags.None,
                0,
                payload.WrittenMemory,
                cancellationToken);
            if (connectNumber > 1 && connectNumber <= _immediatelyDrainedReconnects + 1)
            {
                using var goAway = new PooledByteBufferWriter();
                var lastAccepted = goAway.GetSpan(sizeof(ulong));
                BinaryPrimitives.WriteUInt64LittleEndian(lastAccepted, 0);
                goAway.Advance(sizeof(ulong));
                ProtocolV2PayloadCodec.WriteError(
                    goAway,
                    SharpLinkErrorCode.Unavailable,
                    "immediate rolling restart",
                    1024,
                    out _);
                await connection.InjectFrameAsync(
                    ProtocolV2FrameType.GoAway,
                    ProtocolV2FrameFlags.Error,
                    0,
                    goAway.WrittenMemory,
                    cancellationToken);
            }
            lock (_gate)
                _connections.Add(connection);
            return connection;
        }

        public async Task<TestTransportConnection> WaitForConnectionAsync(int index)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                lock (_gate)
                {
                    if (_connections.Count > index)
                        return _connections[index];
                }
                await Task.Delay(10, timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            TestTransportConnection[] connections;
            lock (_gate)
                connections = [.. _connections];
            for (var index = 0; index < connections.Length; index++)
                await connections[index].DisposeAsync();
        }
    }

    private sealed class BlockingInitialTransportFactory : IClientTransportFactory
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TestTransportConnection? _connection;

        internal TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            var connection = new TestTransportConnection();
            var payload = new PooledByteBufferWriter();
            ProtocolV2PayloadCodec.WriteHandshakeResponse(payload, new ProtocolV2HandshakeResponse(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                4 * 1024 * 1024,
                1024 * 1024,
                16 * 1024 * 1024));
            await connection.InjectFrameAsync(
                ProtocolV2FrameType.HandshakeResponse,
                ProtocolV2FrameFlags.None,
                0,
                payload.WrittenMemory,
                cancellationToken);
            _connection = connection;
            return connection;
        }

        internal void ReleaseConnect() => _release.TrySetResult();

        public ValueTask DisposeAsync()
            => _connection?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class HangingHandshakeTransportFactory : IClientTransportFactory
    {
        private readonly List<TestTransportConnection> _connections = [];

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var connection = new TestTransportConnection();
            _connections.Add(connection);
            return ValueTask.FromResult<ITransportConnection>(connection);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _connections)
                await connection.DisposeAsync();
        }
    }

    private sealed class FixedSnapshotResolver(SharpLinkEndpointSnapshot snapshot) : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(snapshot);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ClientConnection? SelectClusterConnection(
        string runtimeName,
        object stateIndex,
        ClientConnection stale,
        ClientConnection ready)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Public;
        var runtimeType = typeof(SharpLinkClient).GetNestedType(runtimeName, BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find {runtimeName}");
        var endpointType = runtimeType.GetNestedType("EndpointState", flags)
            ?? throw new Exception($"cannot find {runtimeName}.EndpointState");
        var configuration = new StaticEndpointConfiguration(
            new SharpLinkEndpoint
            {
                Id = "selection",
                Address = new SharpLinkTcpAddress("127.0.0.1", 5001)
            },
            new NonConnectingFactory());
        var endpoint = Activator.CreateInstance(
            endpointType,
            BindingFlags.Instance | flags,
            binder: null,
            args: [configuration, stateIndex],
            culture: null)
            ?? throw new Exception($"cannot create {runtimeName}.EndpointState");
        var readyConnections = endpointType.GetField("_readyConnections", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find {runtimeName} ready connection field");
        readyConnections.SetValue(endpoint, new[] { stale, ready });
        var selectConnection = runtimeType.GetMethod(
            "SelectConnection",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find {runtimeName} selection method");
        return (ClientConnection?)selectConnection.Invoke(null, [endpoint]);
    }

    private static SharpLinkEndpoint CreateEndpoint(string id, int port) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress("127.0.0.1", port)
    };

    private sealed class NonConnectingFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AdmitFirstRejectSecondPolicy(TimeSpan retryAfter) : ISharpLinkEndpointAdmissionPolicy
    {
        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
            => endpoint.Endpoint.Id == "first"
                ? new SharpLinkEndpointAdmissionDecision(true, Token: 1, RetryAfter: null)
                : new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: retryAfter);

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }
    }
}
