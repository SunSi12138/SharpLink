using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientReadinessWaitSupport
{
    internal sealed class BlockingInitialTransportFactory : IClientTransportFactory
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TestTransportConnection? _connection;
        private int _connectCount;

        internal TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            ConnectStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);

            var connection = new TestTransportConnection();
            await connection.InjectSuccessfulHandshakeAsync(cancellationToken: cancellationToken);
            _connection = connection;
            return connection;
        }

        internal void ReleaseConnect() => _release.TrySetResult();

        public ValueTask DisposeAsync()
            => _connection?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
