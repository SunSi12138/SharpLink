using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerLifecycleOwnershipCharacterizationTests
{
    [Test]
    public async Task ConnectionServicesShouldRemainOwnedUntilActiveCallsDrain()
    {
        var state = CreateState();
        var service = new TrackingService();
        var scopeFactory = new TrackingScopeFactory();
        var registration = CreateConnectionRegistration(service, scopeFactory);
        Ensure(state.MarkReady(null), "connection ready");
        Ensure(state.TryAcquireCall(1), "active call should acquire connection capacity");
        _ = await state.AcquireServiceAsync(registration, default);
        var scope = scopeFactory.LastCreatedScope
            ?? throw new Exception("connection-scoped service acquisition should create a scope");

        await state.CloseAsync();

        Ensure(state.LifecycleState == ServerConnectionLifecycleState.Closed,
            "transport/session close should complete while an uncooperative call is still active");
        Ensure(!state.ServiceCleanupTask.IsCompleted,
            "connection-scoped service cleanup must wait for active calls to drain");
        Ensure(service.DisposeCount == 0,
            "an active call must keep its connection-scoped service instance alive after session close");
        Ensure(scope.DisposeCount == 0,
            "an active call must keep the connection-scoped IServiceScope alive after session close");

        state.ReleaseCall();
        await state.ServiceCleanupTask.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(service.DisposeCount == 1,
            "connection-scoped services should be released exactly once after the last call drains");
        Ensure(scope.DisposeCount == 1,
            "the connection-scoped IServiceScope should be released exactly once after the last call drains");
    }

    [Test]
    [NotInParallel]
    public async Task ReadyPublicationShouldNotCrossConcurrentDrainBoundary()
    {
        var authentication = new SharpLinkAuthenticationContext(subject: "alice");
        const int delayVariants = 32;
        const int iterationsPerDelay = 8;
        const int spinScale = 64;
        using var phase = new Barrier(3);
        ServerConnectionState? raceState = null;
        var readyPublished = false;
        var finalReadyPublicationCount = 0;
        var losingReadyContextLeakCount = 0;

        var readyWorker = new Thread(() =>
        {
            for (var delay = 0; delay < delayVariants; delay++)
            {
                for (var iteration = 0; iteration < iterationsPerDelay; iteration++)
                {
                    phase.SignalAndWait();
                    var state = Volatile.Read(ref raceState)
                        ?? throw new Exception("race state was not published");
                    Thread.SpinWait(delay * spinScale);
                    Volatile.Write(ref readyPublished, state.MarkReady(authentication));
                    phase.SignalAndWait();
                }
            }
        })
        {
            IsBackground = true,
            Name = "SharpLink connection ready/drain race probe"
        };
        var drainWorker = new Thread(() =>
        {
            for (var delay = 0; delay < delayVariants; delay++)
            {
                for (var iteration = 0; iteration < iterationsPerDelay; iteration++)
                {
                    phase.SignalAndWait();
                    var state = Volatile.Read(ref raceState)
                        ?? throw new Exception("race state was not published");
                    Thread.SpinWait((delayVariants - delay - 1) * spinScale);
                    state.MarkDraining();
                    phase.SignalAndWait();
                }
            }
        })
        {
            IsBackground = true,
            Name = "SharpLink connection drain/ready race probe"
        };
        readyWorker.Start();
        drainWorker.Start();

        for (var delay = 0; delay < delayVariants; delay++)
        {
            for (var iteration = 0; iteration < iterationsPerDelay; iteration++)
            {
                var state = CreateState();
                Volatile.Write(ref raceState, state);
                Volatile.Write(ref readyPublished, false);

                phase.SignalAndWait();
                phase.SignalAndWait();

                if (state.LifecycleState == ServerConnectionLifecycleState.Ready)
                    finalReadyPublicationCount++;
                if (!Volatile.Read(ref readyPublished) &&
                    (state.AuthenticationContext is not null || state.DefaultCallContext is not null))
                {
                    losingReadyContextLeakCount++;
                }

                await state.CloseAsync();
                await state.ServiceCleanupTask;
            }
        }

        readyWorker.Join();
        drainWorker.Join();

        Ensure(finalReadyPublicationCount == 0,
            "once ready publication and drain complete concurrently, lifecycle must never regress to Ready");
        Ensure(losingReadyContextLeakCount == 0,
            "when drain wins the concurrent race, authentication and default call context must not remain published");

        var drainWinningState = CreateState();
        drainWinningState.MarkDraining();
        Ensure(!drainWinningState.MarkReady(authentication),
            "a handshake completing after drain must not publish Ready");
        Ensure(drainWinningState.AuthenticationContext is null && drainWinningState.DefaultCallContext is null,
            "a drain-first handshake completion must roll back authentication and default context");
        await drainWinningState.CloseAsync();
        await drainWinningState.ServiceCleanupTask;

        var readyWinningState = CreateState();
        Ensure(readyWinningState.MarkReady(authentication),
            "the controlled ready-first path must publish Ready before drain begins");
        readyWinningState.MarkDraining();
        Ensure(readyWinningState.LifecycleState == ServerConnectionLifecycleState.Draining,
            "drain must advance an already Ready connection to Draining without lifecycle regression");
        await readyWinningState.CloseAsync();
        await readyWinningState.ServiceCleanupTask;
    }

    [Test]
    [NotInParallel]
    public async Task CallAdmissionShouldNotCrossServerDrainBoundaryWithFreshLifecycleState()
    {
        const int delayVariants = 16;
        const int iterationsPerDelay = 4;
        const int spinScale = 64;
        var lateAdmissionCount = 0;

        for (var delay = 0; delay < delayVariants; delay++)
        {
            for (var iteration = 0; iteration < iterationsPerDelay; iteration++)
            {
                var listener = new BlockingListener();
                await using var server = CreateServer(listener);
                var runTask = server.RunAsync().AsTask();
                await listener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var connection = CreateState();
                Ensure(connection.MarkReady(null), "connection ready");
                using var start = new ManualResetEventSlim(false);

                var admissionTask = Task.Run(() =>
                {
                    start.Wait();
                    Thread.SpinWait(delay * spinScale);
                    return server.TryAcquireCall(connection);
                });
                var stopTask = Task.Run(async () =>
                {
                    start.Wait();
                    Thread.SpinWait((delayVariants - delay - 1) * spinScale);
                    await server.StopAsync(TimeSpan.FromSeconds(2));
                });

                start.Set();
                var admission = await admissionTask.WaitAsync(TimeSpan.FromSeconds(2));
                if (admission == ServerCallAdmissionResult.Acquired)
                {
                    if (server.CallsDrainedForDiagnostics.IsCompletedSuccessfully)
                        lateAdmissionCount++;
                    server.ReleaseCall(connection);
                }

                await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
                await runTask.WaitAsync(TimeSpan.FromSeconds(2));
                Ensure(server.PendingCallAdmissionsForDiagnostics == 0,
                    "pending call-admission ownership must be released after the race");
                Ensure(server.ActiveCallCountForDiagnostics == 0 && connection.ActiveCalls == 0,
                    "server and connection call ownership must converge to zero after the race");
                var drainSnapshot = server.LastCallDrainSignalForDiagnostics
                    ?? throw new Exception("stop must publish a call-drain snapshot");
                Ensure(drainSnapshot.GlobalActiveCalls == 0 &&
                       drainSnapshot.PendingAdmissions == 0 &&
                       drainSnapshot.ReleasingConnectionActiveCalls == 0,
                    "the call-drain publication winner must have observed zero pending, global, and releasing-local ownership");

                await connection.CloseAsync();
                await connection.ServiceCleanupTask;
            }
        }

        Ensure(lateAdmissionCount == 0,
            "a call must never remain acquired after server call drain has already been published");

        var admissionFirstListener = new BlockingListener();
        await using (var admissionFirstServer = CreateServer(admissionFirstListener))
        {
            var runTask = admissionFirstServer.RunAsync().AsTask();
            await admissionFirstListener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var connection = CreateState();
            Ensure(connection.MarkReady(null), "admission-first connection ready");
            Ensure(admissionFirstServer.TryAcquireCall(connection) == ServerCallAdmissionResult.Acquired,
                "admission-first path must acquire before stop starts");
            var stopTask = admissionFirstServer.StopAsync(TimeSpan.FromSeconds(2)).AsTask();
            Ensure(!admissionFirstServer.CallsDrainedForDiagnostics.IsCompleted,
                "server drain must not publish while an acquired call still owns local/global capacity");
            admissionFirstServer.ReleaseCall(connection);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
            await runTask.WaitAsync(TimeSpan.FromSeconds(2));
            await connection.CloseAsync();
            await connection.ServiceCleanupTask;
        }

        var stopFirstListener = new BlockingListener();
        await using (var stopFirstServer = CreateServer(stopFirstListener))
        {
            var runTask = stopFirstServer.RunAsync().AsTask();
            await stopFirstListener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var connection = CreateState();
            Ensure(connection.MarkReady(null), "stop-first connection ready");
            await stopFirstServer.StopAsync(TimeSpan.FromSeconds(2)).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(stopFirstServer.TryAcquireCall(connection) == ServerCallAdmissionResult.Unavailable,
                "admission starting after the stop boundary must be rejected");
            Ensure(stopFirstServer.PendingCallAdmissionsForDiagnostics == 0 &&
                   stopFirstServer.ActiveCallCountForDiagnostics == 0 &&
                   connection.ActiveCalls == 0,
                "stop-first rejection must not publish call ownership");
            await runTask.WaitAsync(TimeSpan.FromSeconds(2));
            await connection.CloseAsync();
            await connection.ServiceCleanupTask;
        }
    }

    [Test]
    [NotInParallel]
    public async Task DeferredRetiredConnectionCleanupMayOutliveServerStopWhenCallOutlivesGrace()
    {
        var listener = new BlockingListener();
        await using var server = CreateServer(listener);
        var runTask = server.RunAsync().AsTask();
        await listener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var connection = CreateState();
        var service = new BlockingTrackingService();
        var registration = CreateConnectionRegistration(service, new TrackingScopeFactory());
        Ensure(connection.MarkReady(null), "connection ready");
        Ensure(server.TryAcquireCall(connection) == ServerCallAdmissionResult.Acquired,
            "the synthetic invocation must own server and connection call capacity");
        _ = await connection.AcquireServiceAsync(registration, default);

        await DisconnectConnectionAsync(server, connection).WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(IsRetiredConnectionTracked(server, connection),
            "deferred cleanup must retain server registry ownership for the retired connection");
        Ensure(server.DeferredTaskSnapshotForDiagnostics.DeferredConnectionCleanups == 1,
            "retiring a connection with an active call must publish deferred connection cleanup ownership");
        Ensure(!connection.ServiceCleanupTask.IsCompleted,
            "connection service cleanup must remain blocked by the active call");

        var stopTask = server.StopAsync(TimeSpan.Zero).AsTask();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!connection.ServiceCleanupTask.IsCompleted,
            "zero-grace server stop may complete while deferred connection cleanup still waits for an active call");
        Ensure(IsRetiredConnectionTracked(server, connection),
            "server stop must not release retired registry ownership before deferred cleanup completes");

        server.ReleaseCall(connection);
        await service.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!connection.ServiceCleanupTask.IsCompleted,
            "connection cleanup should remain observable while service disposal is blocked");
        service.ReleaseDispose();
        await connection.ServiceCleanupTask.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => server.DeferredTaskSnapshotForDiagnostics.DeferredConnectionCleanups == 0,
            "deferred retired connection cleanup did not leave the server observer set");
        await WaitUntilAsync(
            () => !IsRetiredConnectionTracked(server, connection),
            "deferred retired connection cleanup did not release server registry ownership");
        Ensure(service.DisposeCount == 1,
            "deferred retired connection cleanup must dispose the connection service exactly once");
    }

    private static SharpLinkServer CreateServer(IServerTransportListener listener)
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(listener)
            .Build();

    private static ServerConnectionState CreateState()
    {
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            Guid.NewGuid().ToString("N"),
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions());
        return new ServerConnectionState(
            session,
            new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(RpcSessionTestFixture.RuntimeContext.Concurrency),
            CancellationToken.None,
            RpcSessionTestFixture.RuntimeContext.TimeProvider);
    }

    private static async Task DisconnectConnectionAsync(
        SharpLinkServer server,
        ServerConnectionState connection)
    {
        var method = typeof(SharpLinkServer).GetMethod(
            "DisconnectConnectionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find retired connection cleanup path");
        await ((ValueTask)method.Invoke(server, [connection])!).ConfigureAwait(false);
    }

    private static bool IsRetiredConnectionTracked(
        SharpLinkServer server,
        ServerConnectionState connection)
    {
        var field = typeof(SharpLinkServer).GetField(
            "_retiredConnections",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find retired connection registry");
        var retiredConnections =
            (System.Collections.Concurrent.ConcurrentDictionary<ServerConnectionState, byte>)field.GetValue(server)!;
        return retiredConnections.ContainsKey(connection);
    }

    private static ServiceRegistration CreateConnectionRegistration(
        object service,
        TrackingScopeFactory scopeFactory)
        => ServiceRegistration.CreateConnection(
            typeof(object),
            new StubMarker(),
            scopeFactory,
            _ => service,
            disposeService: true);

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException(failureMessage);
    }

    private sealed class BlockingListener : IServerTransportListener
    {
        internal TaskCompletionSource AcceptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public System.Net.EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            AcceptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled accept must not continue.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingTrackingService : IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        internal Task DisposeStarted => _disposeStarted.Task;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal void ReleaseDispose() => _disposeRelease.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            _disposeStarted.TrySetResult();
            await _disposeRelease.Task.ConfigureAwait(false);
        }
    }

    private sealed class TrackingScopeFactory : IServiceScopeFactory
    {
        private TrackingScope? _lastCreatedScope;

        internal TrackingScope? LastCreatedScope => Volatile.Read(ref _lastCreatedScope);

        public IServiceScope CreateScope()
        {
            var scope = new TrackingScope();
            Volatile.Write(ref _lastCreatedScope, scope);
            return scope;
        }
    }

    private sealed class TrackingScope : IServiceScope
    {
        private int _disposeCount;

        public IServiceProvider ServiceProvider { get; } = new EmptyServiceProvider();
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class TrackingService : IAsyncDisposable
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubMarker : IRpcStub
    {
        public long InterfaceHash => 1;

        public ValueTask InvokeNoReturnAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args)
            => ValueTask.CompletedTask;

        public ValueTask InvokeNoReturnCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask InvokeAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output)
            => ValueTask.CompletedTask;

        public ValueTask InvokeCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
