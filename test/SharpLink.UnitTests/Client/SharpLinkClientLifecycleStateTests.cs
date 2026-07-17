using System.Buffers.Binary;
using System.Collections.Generic;
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

        await drainingConnection.InjectFrameAsync(
            ProtocolV2FrameType.GoAway,
            ProtocolV2FrameFlags.Error,
            0,
            payload.WrittenMemory);
        await WaitUntilAsync(() => transport.ConnectCount >= 3 && client.ReadyConnectionCount == 2);

        Ensure(client.State == SharpLinkConnectionState.Ready, "another ready connection should keep the client ready");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, timeout.Token);
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

    private sealed class SequenceClientTransportFactory : IClientTransportFactory
    {
        private readonly Lock _gate = new();
        private readonly List<TestTransportConnection> _connections = [];
        private int _connectCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
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
            lock (_gate)
                _connections.Add(connection);
            Interlocked.Increment(ref _connectCount);
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
