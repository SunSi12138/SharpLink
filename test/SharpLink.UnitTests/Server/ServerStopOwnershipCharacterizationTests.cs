using System.IO.Pipelines;
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

        Ensure(server.HealthStatus == SharpLinkHealthStatus.Unhealthy,
            "first-run cancellation must leave the server in a normal terminal health state without explicit StopAsync");
        Ensure(server.ActiveCallCountForDiagnostics == 1 && connection.ActiveCalls == 1,
            "run cancellation must use zero grace so the run task can complete while an active call still owns capacity");
        Ensure(!server.CallsDrainedForDiagnostics.IsCompleted,
            "zero-grace run cancellation must not forge call-drain completion while the active call remains owned");

        var laterStopTask = server.StopAsync(TimeSpan.FromSeconds(30)).AsTask();
        await laterStopTask.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(server.ActiveCallCountForDiagnostics == 1 && connection.ActiveCalls == 1,
            "a later StopAsync must reuse the cancellation-owned zero-grace shared stop instead of applying a new grace period");

        server.ReleaseCall(connection);
        await server.CallsDrainedForDiagnostics.WaitAsync(TimeSpan.FromSeconds(2));
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
            Ensure(!laterZeroStop.IsCompleted,
                "a later zero-grace StopAsync must not shorten the first owner's graceful wait");

            longFirstServer.ReleaseCall(connection);
            await longFirstStop.WaitAsync(TimeSpan.FromSeconds(2));
            await laterZeroStop.WaitAsync(TimeSpan.FromSeconds(2));
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
