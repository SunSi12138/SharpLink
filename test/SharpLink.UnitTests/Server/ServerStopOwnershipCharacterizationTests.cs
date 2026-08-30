using System.IO.Pipelines;
using System.Reflection;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerStopOwnershipCharacterizationTests
{
    [Test]
    [NotInParallel]
    public async Task FirstRunCancellationShouldOwnZeroGraceSharedStopWithoutExplicitStop()
    {
        var listener = new BlockingListener();
        await using var server = CreateServer(listener);
        using var runCancellation = new CancellationTokenSource();
        var runTask = server.RunAsync(runCancellation.Token).AsTask();
        await listener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var connection = CreateState();
        Ensure(connection.MarkReady(null), "connection ready");
        Ensure(server.TryAcquireCall(connection) == SharpLinkServer.ServerCallAdmissionResult.Acquired,
            "the synthetic invocation must own server and connection call capacity");

        runCancellation.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(GetServerStateName(server) == "Stopped",
            "first-run cancellation must complete the normal zero-grace stop path in Stopped without explicit StopAsync");
        Ensure(server.ActiveCallCountForDiagnostics == 1 && connection.ActiveCalls == 1,
            "run cancellation must use zero grace so the run task can complete while an active call still owns capacity");
        Ensure(!server.CallsDrainedForDiagnostics.IsCompleted,
            "zero-grace run cancellation must not forge call-drain completion while the active call remains owned");

        var laterStopTask = server.StopAsync(TimeSpan.FromSeconds(30)).AsTask();
        await laterStopTask.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(GetServerStateName(server) == "Stopped",
            "a later StopAsync must reuse the already-completed normal stop instead of changing its terminal state");
        Ensure(server.ActiveCallCountForDiagnostics == 1 && connection.ActiveCalls == 1,
            "a later StopAsync must reuse the cancellation-owned zero-grace shared stop instead of applying a new grace period");

        server.ReleaseCall(connection);
        await server.CallsDrainedForDiagnostics.WaitAsync(TimeSpan.FromSeconds(2));
        await connection.CloseAsync();
        await connection.ServiceCleanupTask;
    }

    [Test]
    [NotInParallel]
    public async Task StopCallerCancellationShouldOnlyCancelThatCallerWait()
    {
        var listener = new BlockingListener();
        await using var server = CreateServer(listener);
        var runTask = server.RunAsync().AsTask();
        await listener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var connection = CreateState();
        Ensure(connection.MarkReady(null), "connection ready");
        Ensure(server.TryAcquireCall(connection) == SharpLinkServer.ServerCallAdmissionResult.Acquired,
            "the synthetic invocation must own server and connection call capacity");

        using var callerCancellation = new CancellationTokenSource();
        var cancelledCallerWait = server.StopAsync(TimeSpan.FromSeconds(30), callerCancellation.Token).AsTask();
        Ensure(GetServerStateName(server) == "Draining",
            "the long-grace stop must establish shared cleanup and enter Draining while the active call is owned");

        var sharedStopBeforeCancellation = GetSharedStopTask(server);
        Ensure(sharedStopBeforeCancellation is not null && !sharedStopBeforeCancellation.IsCompleted,
            "the first StopAsync caller must establish an in-flight shared stop task");

        callerCancellation.Cancel();
        var callerObservedCancellation = false;
        try
        {
            await cancelledCallerWait.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            callerObservedCancellation = true;
        }

        Ensure(callerObservedCancellation,
            "cancelling the StopAsync caller token must cancel that caller's wait");
        Ensure(ReferenceEquals(sharedStopBeforeCancellation, GetSharedStopTask(server)),
            "caller cancellation must not replace or cancel the shared stop cleanup task");
        Ensure(!sharedStopBeforeCancellation.IsCompleted && GetServerStateName(server) == "Draining",
            "shared cleanup must remain alive in Draining after the first caller cancels its wait");
        Ensure(server.ActiveCallCountForDiagnostics == 1 && connection.ActiveCalls == 1,
            "caller cancellation must not release or bypass active-call ownership");

        var laterStop = server.StopAsync(TimeSpan.FromSeconds(30)).AsTask();
        Ensure(ReferenceEquals(sharedStopBeforeCancellation, laterStop),
            "an uncancelled later StopAsync caller must join the original shared cleanup");
        Ensure(!laterStop.IsCompleted,
            "the surviving shared cleanup must continue waiting for the active call under the original grace period");

        server.ReleaseCall(connection);
        await laterStop.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(GetServerStateName(server) == "Stopped",
            "the original shared cleanup must complete normally after the active call releases ownership");
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
        await connection.CloseAsync();
        await connection.ServiceCleanupTask;
    }

    [Test]
    [NotInParallel]
    public async Task FirstStopOwnerShouldOwnSharedGraceTimeout()
    {
        var longFirstListener = new BlockingListener();
        await using (var longFirstServer = CreateServer(longFirstListener))
        {
            var runTask = longFirstServer.RunAsync().AsTask();
            await longFirstListener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var connection = CreateState();
            Ensure(connection.MarkReady(null), "long-first connection ready");
            Ensure(longFirstServer.TryAcquireCall(connection) == SharpLinkServer.ServerCallAdmissionResult.Acquired,
                "long-first call must own capacity before stop begins");

            var longFirstStop = longFirstServer.StopAsync(TimeSpan.FromSeconds(30)).AsTask();
            Ensure(longFirstServer.HealthStatus == SharpLinkHealthStatus.Draining,
                "the first long-grace stop must enter Draining while the call is still active");
            var laterZeroStop = longFirstServer.StopAsync(TimeSpan.Zero).AsTask();
            Ensure(ReferenceEquals(longFirstStop, laterZeroStop),
                "later StopAsync calls must reuse the shared task established by the first stop owner");

            var zeroOverrideWindow = Task.Delay(TimeSpan.FromSeconds(1));
            Ensure(await Task.WhenAny(laterZeroStop, zeroOverrideWindow) == zeroOverrideWindow,
                "a later zero-grace StopAsync must not shorten the first owner's graceful wait while the active call remains owned");
            Ensure(longFirstServer.HealthStatus == SharpLinkHealthStatus.Draining &&
                   longFirstServer.ActiveCallCountForDiagnostics == 1 &&
                   connection.ActiveCalls == 1,
                "the shared stop must remain in its first owner's long-grace drain after the later zero-grace caller has had time to run");

            longFirstServer.ReleaseCall(connection);
            await longFirstStop.WaitAsync(TimeSpan.FromSeconds(2));
            await laterZeroStop.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(GetServerStateName(longFirstServer) == "Stopped",
                "long-first shared stop should complete normally after the active call releases ownership");
            await runTask.WaitAsync(TimeSpan.FromSeconds(2));
            await connection.CloseAsync();
            await connection.ServiceCleanupTask;
        }

        var zeroFirstListener = new BlockingListener();
        await using (var zeroFirstServer = CreateServer(zeroFirstListener))
        {
            var runTask = zeroFirstServer.RunAsync().AsTask();
            await zeroFirstListener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var connection = CreateState();
            Ensure(connection.MarkReady(null), "zero-first connection ready");
            Ensure(zeroFirstServer.TryAcquireCall(connection) == SharpLinkServer.ServerCallAdmissionResult.Acquired,
                "zero-first call must own capacity before stop begins");

            var zeroFirstStop = zeroFirstServer.StopAsync(TimeSpan.Zero).AsTask();
            var laterLongStop = zeroFirstServer.StopAsync(TimeSpan.FromSeconds(30)).AsTask();
            Ensure(ReferenceEquals(zeroFirstStop, laterLongStop),
                "a later long-grace StopAsync must reuse the zero-grace task established by the first stop owner");
            await zeroFirstStop.WaitAsync(TimeSpan.FromSeconds(2));
            await laterLongStop.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(GetServerStateName(zeroFirstServer) == "Stopped",
                "zero-first shared stop must reach the normal Stopped terminal state without waiting for the later grace period");
            Ensure(zeroFirstServer.ActiveCallCountForDiagnostics == 1 && connection.ActiveCalls == 1,
                "a later long grace period must not extend the first owner's zero-grace stop while the call remains active");
            await runTask.WaitAsync(TimeSpan.FromSeconds(2));

            zeroFirstServer.ReleaseCall(connection);
            await zeroFirstServer.CallsDrainedForDiagnostics.WaitAsync(TimeSpan.FromSeconds(2));
            await connection.CloseAsync();
            await connection.ServiceCleanupTask;
        }
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

    private static Task? GetSharedStopTask(SharpLinkServer server)
    {
        var stopTaskField = typeof(SharpLinkServer).GetField(
            "_stopTask",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find shared stop task");
        return (Task?)stopTaskField.GetValue(server);
    }

    private static string GetServerStateName(SharpLinkServer server)
    {
        var stateField = typeof(SharpLinkServer).GetField(
            "_state",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find server lifecycle state");
        var stateType = typeof(SharpLinkServer).GetNestedType(
            "ServerState",
            BindingFlags.NonPublic)
            ?? throw new Exception("cannot find server lifecycle enum");
        var stateValue = (int)stateField.GetValue(server)!;
        return Enum.GetName(stateType, stateValue)
            ?? throw new Exception($"unknown server lifecycle state value {stateValue}");
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
