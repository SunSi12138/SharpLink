using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Sockets;
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
}
