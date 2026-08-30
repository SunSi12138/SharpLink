using System.Collections.Generic;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientLifecycleStartStopSupport
{
    internal static async Task EnsureCancelledAsync(Task operation)
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

    internal static bool ContainsHandshakeTimeout(Exception exception)
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

    internal sealed class BlockingInitialTransportFactory : IClientTransportFactory
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

    internal sealed class HangingHandshakeTransportFactory : IClientTransportFactory
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

    internal sealed class FixedSnapshotResolver(SharpLinkEndpointSnapshot snapshot) : ISharpLinkEndpointResolver
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

    internal sealed class CleanupFailingHandshakeTransportFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ITransportConnection>(new CleanupFailingConnection());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class InitialPoolRollbackFailingTransportFactory : IClientTransportFactory
    {
        private int _connectCount;

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _connectCount) != 1)
                throw new InvalidOperationException("second connection failed");

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
            return new CleanupFailingReadyConnection(connection);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CleanupFailingReadyConnection(TestTransportConnection inner) : ITransportConnection
    {
        public string Id => inner.Id;
        public System.IO.Pipelines.PipeReader Input => inner.Input;
        public System.IO.Pipelines.PipeWriter Output => inner.Output;
        public System.Net.EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public System.Net.EndPoint? RemoteEndPoint => inner.RemoteEndPoint;

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            throw new InvalidOperationException("ready connection cleanup failed");
        }
    }

    private sealed class CleanupFailingConnection : ITransportConnection
    {
        private readonly System.IO.Pipelines.Pipe _input = new();
        private readonly System.IO.Pipelines.Pipe _output = new();

        internal CleanupFailingConnection() => _input.Writer.Complete();

        public string Id { get; } = "cleanup-failing";
        public System.IO.Pipelines.PipeReader Input => _input.Reader;
        public System.IO.Pipelines.PipeWriter Output => _output.Writer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public System.Net.EndPoint? RemoteEndPoint => null;
        public ValueTask DisposeAsync()
            => ValueTask.FromException(new InvalidOperationException("transport cleanup failed"));
    }
}
