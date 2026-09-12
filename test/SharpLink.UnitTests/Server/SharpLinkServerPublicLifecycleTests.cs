using System.IO.Pipelines;
using System.Reflection;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

[NotInParallel]
public sealed class SharpLinkServerPublicLifecycleTests
{
    [Test]
    public async Task StartAndShutdownWaitShouldHaveIndependentOwnership()
    {
        var listener = new BlockingListener();
        await using var server = SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(listener)
            .Build();

        await server.StartAsync();
        Ensure(server.LifecycleState == SharpLinkServerLifecycleState.Running,
            "StartAsync must publish Running only after accept infrastructure is active");
        Ensure(server.HealthStatus == SharpLinkHealthStatus.Ready,
            "Running serving surface must publish local readiness");

        var shutdownWait = server.WaitForShutdownAsync();
        Ensure(!shutdownWait.IsCompleted,
            "WaitForShutdownAsync must not initiate shutdown");
        using (var waitCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await EnsureCancelledAsync(server.WaitForShutdownAsync(waitCancellation.Token));
        Ensure(server.LifecycleState == SharpLinkServerLifecycleState.Running,
            "canceling one shutdown waiter must not change Server lifetime");

        await server.StopAsync(TimeSpan.Zero);
        await shutdownWait.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(server.LifecycleState == SharpLinkServerLifecycleState.Stopped,
            "StopAsync must own the Draining to Stopped transition");
    }

    [Test]
    public async Task ImmediateAcceptFailureShouldFailStartupAndTerminalWait()
    {
        var server = SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new ImmediateFailureListener())
            .Build();

        var startFailure = await CaptureFailureAsync(server.StartAsync().AsTask());
        Ensure(startFailure is IOException { Message: "startup accept failed" },
            "an immediate accept infrastructure failure must surface from StartAsync");
        Ensure(server.LifecycleState == SharpLinkServerLifecycleState.Faulted,
            "startup infrastructure failure must terminate the local runtime as Faulted");

        var terminalFailure = await CaptureFailureAsync(server.WaitForShutdownAsync());
        Ensure(terminalFailure is IOException { Message: "startup accept failed" },
            "WaitForShutdownAsync must propagate the terminal runtime failure");
    }

    [Test]
    public async Task StopFromCreatedShouldCompleteShutdownWait()
    {
        await using var server = SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new BlockingListener())
            .Build();
        var shutdownWait = server.WaitForShutdownAsync();

        await server.StopAsync(TimeSpan.Zero);
        await shutdownWait.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(server.LifecycleState == SharpLinkServerLifecycleState.Stopped,
            "StopAsync from Created must still produce a real terminal Stopped state");
    }

    [Test]
    public async Task StopCallerCancellationMustNotCancelSharedShutdown()
    {
        var listener = new DelayedDisposeListener();
        await using var server = SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(listener)
            .Build();
        await server.StartAsync();

        var owner = server.StopAsync(TimeSpan.Zero).AsTask();
        await listener.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await EnsureCancelledAsync(server.StopAsync(TimeSpan.Zero, cancelled.Token).AsTask());
        Ensure(!owner.IsCompleted,
            "caller cancellation must not cancel the shared stop operation");

        listener.ReleaseDispose();
        await owner.WaitAsync(TimeSpan.FromSeconds(2));
        await server.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(server.LifecycleState == SharpLinkServerLifecycleState.Stopped,
            "shared shutdown must continue to completion after a waiter cancels");
    }

    [Test]
    public async Task RuntimeAcceptFailureShouldCompleteSharedStopAndTerminalWaitTogether()
    {
        var listener = new DeferredFailureListener();
        var server = SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(listener)
            .Build();
        await server.StartAsync();
        var shutdownWait = server.WaitForShutdownAsync();

        listener.Fail(new IOException("runtime accept failed"));
        await WaitForLifecycleAsync(server, SharpLinkServerLifecycleState.Faulted);
        var stopFailure = await CaptureFailureAsync(server.StopAsync(TimeSpan.Zero).AsTask());

        Ensure(stopFailure is IOException { Message: "runtime accept failed" },
            "StopAsync must join and surface the runtime-owned terminal failure");
        Ensure(shutdownWait.IsCompleted,
            "a completed shared stop task must already have published terminal completion");
        var terminalFailure = await CaptureFailureAsync(shutdownWait);
        Ensure(terminalFailure is IOException { Message: "runtime accept failed" },
            "terminal wait must surface the same runtime failure");
    }

    [Test]
    public async Task FatalRuntimeFailureWaitOnlyShouldObserveOwnedTerminalCleanupTask()
    {
        var listener = new DeferredFailureListener();
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(listener)
            .Build();
        await server.StartAsync();

        listener.Fail(new IOException("wait-only runtime failure"));
        var terminalFailure = await CaptureFailureAsync(server.WaitForShutdownAsync());

        Ensure(terminalFailure is IOException { Message: "wait-only runtime failure" },
            "WaitForShutdownAsync must surface the fatal runtime failure without requiring StopAsync");
        var observer = server.LifecycleForDiagnostics.TerminalFailureObserverTaskForDiagnostics
            ?? throw new Exception("fatal runtime cleanup must have a Server-owned observer");
        await observer.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(observer.IsCompletedSuccessfully,
            "the Server-owned observer must consume the shared stop task failure");
        Ensure(server.LifecycleState == SharpLinkServerLifecycleState.Faulted,
            "wait-only terminal observation must leave the shared lifecycle Faulted");
    }

    [Test]
    public async Task ConnectionCleanupFailureShouldNotTerminateServerRuntime()
    {
        var listener = new ConnectionFailureIsolationListener();
        await using var server = SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(listener)
            .Build();

        await server.StartAsync();
        await listener.FirstConnection.DisposeObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await listener.SecondAccepted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var terminalProbe = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var terminalProbeFailure = await CaptureFailureAsync(
            server.WaitForShutdownAsync(terminalProbe.Token));

        Ensure(terminalProbeFailure is OperationCanceledException,
            "a per-connection cleanup failure must not terminate the shared Server runtime");
        Ensure(server.LifecycleState == SharpLinkServerLifecycleState.Running,
            "a per-connection cleanup failure must keep Server lifecycle Running");
        Ensure(listener.AcceptCount >= 3,
            "the accept loop must continue after a connection cleanup failure");
    }

    [Test]
    public async Task HeartbeatConnectionCleanupFailureShouldRemainConnectionLocal()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new BlockingListener())
            .Build();
        await server.StartAsync();

        var transport = new HeartbeatFailingDisposeConnection();
        var session = new RpcSession(transport, RpcSessionTestFixture.ServerOptions());
        var connection = new ServerConnectionState(
            session,
            new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(RpcSessionTestFixture.RuntimeContext.Concurrency),
            CancellationToken.None,
            RpcSessionTestFixture.RuntimeContext.TimeProvider);
        var disconnectHeartbeat = typeof(SharpLinkServer).GetMethod(
            "DisconnectHeartbeatTimedOutConnectionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find heartbeat connection cleanup boundary");

        var cleanup = (Task)disconnectHeartbeat.Invoke(server, [connection, CancellationToken.None])!;
        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(transport.DisposeObserved.Task.IsCompleted,
            "heartbeat path must attempt connection cleanup");
        Ensure(server.LifecycleState == SharpLinkServerLifecycleState.Running,
            "heartbeat connection cleanup failure must not fault the Server runtime");
    }

    private static async Task WaitForLifecycleAsync(
        ISharpLinkServer server,
        SharpLinkServerLifecycleState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (server.LifecycleState != expected)
            await Task.Delay(10, timeout.Token);
    }

    private static async Task EnsureCancelledAsync(Task task)
    {
        var failure = await CaptureFailureAsync(task);
        Ensure(failure is OperationCanceledException,
            $"expected cancellation, got {failure?.GetType().Name ?? "success"}");
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class BlockingListener : IServerTransportListener
    {
        internal TaskCompletionSource AcceptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Net.EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(
            CancellationToken cancellationToken = default)
        {
            AcceptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ImmediateFailureListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(
                new IOException("startup accept failed"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DeferredFailureListener : IServerTransportListener
    {
        private readonly TaskCompletionSource<ITransportConnection> _accept =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public System.Net.EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(
            CancellationToken cancellationToken = default)
            => new(_accept.Task.WaitAsync(cancellationToken));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void Fail(Exception exception) => _accept.TrySetException(exception);
    }

    private sealed class ConnectionFailureIsolationListener : IServerTransportListener
    {
        private int _acceptCount;

        internal FailingDisposeConnection FirstConnection { get; } = new();
        internal TaskCompletionSource SecondAccepted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int AcceptCount => Volatile.Read(ref _acceptCount);
        public System.Net.EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(
            CancellationToken cancellationToken = default)
        {
            var acceptCount = Interlocked.Increment(ref _acceptCount);
            if (acceptCount == 1)
                return FirstConnection;
            if (acceptCount == 2)
            {
                SecondAccepted.TrySetResult();
                return new BlockingConnection("second");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingDisposeConnection : ITransportConnection
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();

        internal FailingDisposeConnection()
        {
            _input.Writer.Complete();
        }

        internal TaskCompletionSource DisposeObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "failing-cleanup";
        public PipeReader Input => _input.Reader;
        public PipeWriter Output => _output.Writer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public System.Net.EndPoint? RemoteEndPoint => null;

        public ValueTask DisposeAsync()
        {
            DisposeObserved.TrySetResult();
            return ValueTask.FromException(new IOException("connection cleanup failed"));
        }
    }

    private sealed class BlockingConnection(string id) : ITransportConnection
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();

        public string Id { get; } = id;
        public PipeReader Input => _input.Reader;
        public PipeWriter Output => _output.Writer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public System.Net.EndPoint? RemoteEndPoint => null;

        public async ValueTask DisposeAsync()
        {
            await _input.Writer.CompleteAsync().ConfigureAwait(false);
            await _output.Reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private sealed class HeartbeatFailingDisposeConnection : ITransportConnection
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();

        internal TaskCompletionSource DisposeObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "heartbeat-failing-cleanup";
        public PipeReader Input => _input.Reader;
        public PipeWriter Output => _output.Writer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public System.Net.EndPoint? RemoteEndPoint => null;

        public ValueTask DisposeAsync()
        {
            DisposeObserved.TrySetResult();
            return ValueTask.FromException(new IOException("heartbeat connection cleanup failed"));
        }
    }

    private sealed class DelayedDisposeListener : IServerTransportListener
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public System.Net.EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public async ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }

        internal void ReleaseDispose() => _release.TrySetResult();
    }
}
