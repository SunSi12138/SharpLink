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
        await connection.InjectSuccessfulHandshakeAsync(cancellationToken: cancellationToken);
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
        private readonly TaskCompletionSource _shutdown =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SharpLinkClientLifecycleState LifecycleState { get; private set; } = SharpLinkClientLifecycleState.Created;
        public SharpLinkReadinessState Readiness => SharpLinkReadinessState.NotReady;
        public SharpLinkClusterState ClusterState => LifecycleState == SharpLinkClientLifecycleState.Stopped
            ? SharpLinkClusterState.Stopped : SharpLinkClusterState.Inactive;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LifecycleState == SharpLinkClientLifecycleState.Stopped)
                throw new InvalidOperationException("This local test runtime has stopped.");
            LifecycleState = SharpLinkClientLifecycleState.Running;
            return ValueTask.CompletedTask;
        }

        // This metadata/capability fixture has no remote connectivity owner.
        public ValueTask WaitForReadyAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException(new NotSupportedException("This fixture has no remote readiness operation."));
        public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
            => _shutdown.Task.WaitAsync(cancellationToken);


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
        {
            LifecycleState = SharpLinkClientLifecycleState.Stopped;
            _shutdown.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkHealthCheckResult>(new NotSupportedException());

        // This fixture exercises lifecycle/readiness only, never contract construction.
        public IRpcRuntimeContext RuntimeContext => throw new NotSupportedException();

        public RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null)
            where TContract : IService => throw new NotSupportedException();

        public RpcLocalContract GetLocalContract<TContract>() where TContract : IService
            => throw new NotSupportedException();

        public RpcLocalContract GetLocalContract(Assembly registeredAssembly, long contractId)
            => throw new NotSupportedException();

        public IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => StopAsync();
    }
}
