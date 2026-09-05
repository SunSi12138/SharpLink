using System.Collections.Generic;
using System.Reflection;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientReadinessStateSupport
{
    internal static async Task<SharpLinkClientReadinessSnapshot> WaitForReadinessSnapshotAsync(
        SharpLinkClient client,
        Func<SharpLinkClientReadinessSnapshot, bool> predicate)
    {
        while (true)
        {
            var publication = client.ReadinessPublicationForTesting;
            if (predicate(publication.Snapshot))
                return publication.Snapshot;
            await publication.Changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async ValueTask<TestTransportConnection> CreateReadyConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new TestTransportConnection();
        using var payload = new PooledByteBufferWriter();
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
        return connection;
    }

    internal sealed class ControlledSequenceTransportFactory : IClientTransportFactory
    {
        private readonly Lock _gate = new();
        private readonly List<TestTransportConnection> _connections = [];
        private readonly bool _blockFirstAttempt;
        private readonly Exception? _firstFailure;
        private readonly bool _blockLaterAttempts;
        private readonly TaskCompletionSource _firstRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _laterRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;
        private int _blockDispose;

        internal ControlledSequenceTransportFactory(
            bool blockFirstAttempt = false,
            Exception? firstFailure = null,
            bool blockLaterAttempts = false)
        {
            _blockFirstAttempt = blockFirstAttempt;
            _firstFailure = firstFailure;
            _blockLaterAttempts = blockLaterAttempts;
        }

        internal TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource LaterAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<TestTransportConnection> FirstConnectionCreated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _connectCount);
            if (attempt == 1)
            {
                FirstAttemptStarted.TrySetResult();
                if (_blockFirstAttempt)
                    await _firstRelease.Task.WaitAsync(cancellationToken);
                if (_firstFailure is not null)
                    throw _firstFailure;
            }
            else
            {
                LaterAttemptStarted.TrySetResult();
                if (_blockLaterAttempts)
                    await _laterRelease.Task.WaitAsync(cancellationToken);
            }

            var connection = await CreateReadyConnectionAsync(cancellationToken);
            lock (_gate)
                _connections.Add(connection);
            if (attempt == 1)
                FirstConnectionCreated.TrySetResult(connection);
            return connection;
        }

        internal void ReleaseFirstAttempt() => _firstRelease.TrySetResult();

        internal void ReleaseLaterAttempts() => _laterRelease.TrySetResult();

        internal void BlockDispose() => Volatile.Write(ref _blockDispose, 1);

        internal void ReleaseDispose() => _disposeRelease.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            _firstRelease.TrySetResult();
            _laterRelease.TrySetResult();
            DisposeStarted.TrySetResult();
            if (Volatile.Read(ref _blockDispose) != 0)
                await _disposeRelease.Task;
            TestTransportConnection[] connections;
            lock (_gate)
                connections = [.. _connections];
            for (var index = 0; index < connections.Length; index++)
                await connections[index].DisposeAsync();
        }
    }

    internal sealed class FixedReadinessReconnectJitter(TimeSpan delay) : ISharpLinkReconnectJitter
    {
        public TimeSpan AddQuarterWindow(int baseDelayMilliseconds)
        {
            _ = baseDelayMilliseconds;
            return delay;
        }

        public TimeSpan ScaleTwentyPercent(int baseDelayMilliseconds)
        {
            _ = baseDelayMilliseconds;
            return delay;
        }
    }

    internal sealed class LegacyThirdPartyClient : ISharpLinkClient
    {
        public SharpLinkConnectionState State => SharpLinkConnectionState.Created;

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyUnregisterResult>(new NotSupportedException());

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyReplacementResult>(new NotSupportedException());

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkHealthCheckResult>(new NotSupportedException());

        public TContract Get<TContract>() where TContract : IService
            => throw new NotSupportedException();

        public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
