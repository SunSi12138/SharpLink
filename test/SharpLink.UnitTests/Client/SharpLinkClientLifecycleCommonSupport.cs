using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientLifecycleSharedSupport
{
    internal static SharpClientBuilder CreateClientBuilder()
        => SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableRequestTimeout();

    internal static ClientConnection GetOnlyReadyConnection(SharpLinkClient client)
    {
        var readyConnectionsField = typeof(SharpLinkClient).GetField(
            "_readyConnections",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find ready connection field");
        var connections = (ClientConnection[])readyConnectionsField.GetValue(client)!;
        Ensure(connections.Length == 1,
            "the deterministic lifecycle scenario requires exactly one ready connection");
        return connections[0];
    }

    internal static async Task WaitUntilAsync(
        Func<bool> condition,
        Func<string>? timeoutMessage = null,
        TimeSpan? timeout = null)
    {
        using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
        try
        {
            while (!condition())
                await Task.Delay(10, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage?.Invoke() ?? "The expected client state was not reached.");
        }
    }

    internal static async Task<SharpLinkException> CaptureSharpLinkExceptionAsync(Task operation)
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

    internal static bool ContainsException(Exception exception, Func<Exception, bool> predicate)
    {
        if (predicate(exception))
            return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var innerException in aggregate.InnerExceptions)
            {
                if (ContainsException(innerException, predicate))
                    return true;
            }
            return false;
        }
        return exception.InnerException is { } inner && ContainsException(inner, predicate);
    }

    internal static SharpLinkEndpoint CreateEndpoint(string id, int port) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress("127.0.0.1", port)
    };

    internal static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    internal sealed class NonConnectingFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class FixedReconnectJitter(TimeSpan delay) : ISharpLinkReconnectJitter
    {
        private int _addQuarterWindowCalls;
        private int _scaleTwentyPercentCalls;

        internal int AddQuarterWindowCalls => Volatile.Read(ref _addQuarterWindowCalls);
        internal int ScaleTwentyPercentCalls => Volatile.Read(ref _scaleTwentyPercentCalls);

        public TimeSpan AddQuarterWindow(int baseDelayMilliseconds)
        {
            _ = baseDelayMilliseconds;
            Interlocked.Increment(ref _addQuarterWindowCalls);
            return delay;
        }

        public TimeSpan ScaleTwentyPercent(int baseDelayMilliseconds)
        {
            _ = baseDelayMilliseconds;
            Interlocked.Increment(ref _scaleTwentyPercentCalls);
            return delay;
        }
    }

    internal sealed class SequenceClientTransportFactory : IClientTransportFactory
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

        public int ConnectionCount
        {
            get
            {
                lock (_gate)
                    return _connections.Count;
            }
        }

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
