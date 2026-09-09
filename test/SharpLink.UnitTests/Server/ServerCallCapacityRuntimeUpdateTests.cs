using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Reflection;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class ServerCallCapacityRuntimeUpdateTests
{
    [Test]
    public async Task ServerCapacityIncreaseAndShrinkPreserveOneMultiConnectionAccountingDomain()
    {
        await using var server = CreateServer(
            maxConcurrentCallsPerConnection: 4,
            maxConcurrentCallsPerServer: 2);
        await using var firstSession = CreateSession("capacity-server-first");
        await using var secondSession = CreateSession("capacity-server-second");
        await using var thirdSession = CreateSession("capacity-server-third");
        await using var fourthSession = CreateSession("capacity-server-fourth");
        var first = CreateReadyConnection(firstSession);
        var second = CreateReadyConnection(secondSession);
        var third = CreateReadyConnection(thirdSession);
        var fourth = CreateReadyConnection(fourthSession);

        Ensure(server.TryAcquireCall(first) == ServerCallAdmissionResult.Acquired, "first call");
        Ensure(server.TryAcquireCall(second) == ServerCallAdmissionResult.Acquired, "second call");
        Ensure(
            server.TryAcquireCall(third) == ServerCallAdmissionResult.ServerCapacityExhausted,
            "the initial server target must reject a third connection");

        server.UpdateCallCapacity(4, 3);
        Ensure(
            server.TryAcquireCall(third) == ServerCallAdmissionResult.Acquired,
            "server growth must expose the additional slot immediately");

        server.UpdateCallCapacity(4, 1);
        Ensure(server.ActiveCallCountForDiagnostics == 3, "shrink must not preempt active calls");
        Ensure(
            server.TryAcquireCall(fourth) == ServerCallAdmissionResult.ServerCapacityExhausted,
            "usage above the shrunken server target must reject new calls");

        server.ReleaseCall(first);
        server.ReleaseCall(second);
        Ensure(server.ActiveCallCountForDiagnostics == 1, "existing calls must drain naturally");
        Ensure(
            server.TryAcquireCall(fourth) == ServerCallAdmissionResult.ServerCapacityExhausted,
            "usage equal to the target must not grant another slot");

        server.ReleaseCall(third);
        Ensure(
            server.TryAcquireCall(fourth) == ServerCallAdmissionResult.Acquired,
            "admission must resume once usage falls below the target");
        server.ReleaseCall(fourth);

        await Assert.That(server.ActiveCallCountForDiagnostics).IsEqualTo(0);
        await Assert.That(server.MaxConcurrentCallsPerServerForDiagnostics).IsEqualTo(1);
    }

    [Test]
    public async Task PerConnectionIncreaseAndShrinkKeepTheConnectionReusableAfterRejection()
    {
        await using var server = CreateServer(
            maxConcurrentCallsPerConnection: 1,
            maxConcurrentCallsPerServer: 8);
        await using var session = CreateSession("capacity-connection");
        var connection = CreateReadyConnection(session);

        Ensure(server.TryAcquireCall(connection) == ServerCallAdmissionResult.Acquired, "first call");
        Ensure(
            server.TryAcquireCall(connection) == ServerCallAdmissionResult.PerConnectionCapacityExhausted,
            "the initial per-connection target must reject a second call");

        server.UpdateCallCapacity(3, 8);
        Ensure(server.TryAcquireCall(connection) == ServerCallAdmissionResult.Acquired, "second call after growth");
        Ensure(server.TryAcquireCall(connection) == ServerCallAdmissionResult.Acquired, "third call after growth");

        server.UpdateCallCapacity(1, 8);
        Ensure(connection.ActiveCalls == 3, "per-connection shrink must not preempt active calls");
        Ensure(
            server.TryAcquireCall(connection) == ServerCallAdmissionResult.PerConnectionCapacityExhausted,
            "usage above the new target must remain closed");

        server.ReleaseCall(connection);
        server.ReleaseCall(connection);
        Ensure(connection.ActiveCalls == 1, "two calls must drain without cancellation");
        Ensure(
            server.TryAcquireCall(connection) == ServerCallAdmissionResult.PerConnectionCapacityExhausted,
            "usage equal to the target must remain closed");

        server.ReleaseCall(connection);
        Ensure(
            server.TryAcquireCall(connection) == ServerCallAdmissionResult.Acquired,
            "the same healthy connection must be reusable after a capacity rejection");
        server.ReleaseCall(connection);

        await Assert.That(connection.ActiveCalls).IsEqualTo(0);
        await Assert.That(server.MaxConcurrentCallsPerConnectionForDiagnostics).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidMultiFieldUpdatePublishesNothing()
    {
        await using var server = CreateServer(
            maxConcurrentCallsPerConnection: 2,
            maxConcurrentCallsPerServer: 4);

        var failure = await Assert.ThrowsAsync(() =>
        {
            server.UpdateCallCapacity(3, 0);
            return Task.CompletedTask;
        });

        await Assert.That(failure).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(server.MaxConcurrentCallsPerConnectionForDiagnostics).IsEqualTo(2);
        await Assert.That(server.MaxConcurrentCallsPerServerForDiagnostics).IsEqualTo(4);
    }

    [Test]
    public async Task RepeatedConcurrentUpdatesAndAcquisitionsReturnAuthoritativeCountersToZero()
    {
        await using var server = CreateServer(
            maxConcurrentCallsPerConnection: 64,
            maxConcurrentCallsPerServer: 64);
        await using var firstSession = CreateSession("capacity-race-first");
        await using var secondSession = CreateSession("capacity-race-second");
        await using var thirdSession = CreateSession("capacity-race-third");
        await using var fourthSession = CreateSession("capacity-race-fourth");
        var connections = new[]
        {
            CreateReadyConnection(firstSession),
            CreateReadyConnection(secondSession),
            CreateReadyConnection(thirdSession),
            CreateReadyConnection(fourthSession)
        };
        var failures = new ConcurrentQueue<Exception>();
        var workers = new Thread[5];

        workers[0] = new Thread(() =>
        {
            try
            {
                for (var index = 0; index < 10000; index++)
                {
                    var target = (index & 1) == 0 ? 1 : 64;
                    server.UpdateCallCapacity(target, target);
                }
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        });

        for (var workerIndex = 1; workerIndex < workers.Length; workerIndex++)
        {
            var connection = connections[workerIndex - 1];
            workers[workerIndex] = new Thread(() =>
            {
                try
                {
                    for (var index = 0; index < 5000; index++)
                    {
                        if (server.TryAcquireCall(connection) == ServerCallAdmissionResult.Acquired)
                            server.ReleaseCall(connection);
                    }
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            });
        }

        foreach (var worker in workers)
            worker.Start();
        foreach (var worker in workers)
            worker.Join();

        Ensure(failures.IsEmpty, "concurrent update/acquire/release must not throw");
        server.AssertCallAccountingInvariant();
        await Assert.That(server.ActiveCallCountForDiagnostics).IsEqualTo(0);
        foreach (var connection in connections)
            await Assert.That(connection.ActiveCalls).IsEqualTo(0);
    }

    [Test]
    public async Task DrainingServerRejectsFurtherCapacityPublication()
    {
        await using var server = CreateServer(
            maxConcurrentCallsPerConnection: 2,
            maxConcurrentCallsPerServer: 4);
        SetServerState(server, draining: true);

        var failure = await Assert.ThrowsAsync(() =>
        {
            server.UpdateCallCapacity(3, 6);
            return Task.CompletedTask;
        });

        await Assert.That(failure).IsTypeOf<InvalidOperationException>();
        await Assert.That(server.MaxConcurrentCallsPerConnectionForDiagnostics).IsEqualTo(2);
        await Assert.That(server.MaxConcurrentCallsPerServerForDiagnostics).IsEqualTo(4);
    }

    private static SharpLinkServer CreateServer(
        int maxConcurrentCallsPerConnection,
        int maxConcurrentCallsPerServer)
    {
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseRuntime(options =>
            {
                options.FlowControl.MaxConcurrentCallsPerConnection = maxConcurrentCallsPerConnection;
                options.FlowControl.MaxConcurrentCallsPerServer = maxConcurrentCallsPerServer;
            })
            .UseTransport(new IdleListener())
            .Build();
        SetServerState(server, draining: false);
        return server;
    }

    private static void SetServerState(SharpLinkServer server, bool draining)
        => typeof(SharpLinkServer).GetField(
                "_state",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, draining ? 3 : 2);

    private static RpcSession CreateSession(string id)
    {
        var input = new Pipe();
        var output = new Pipe();
        return RpcSessionTestFixture.CreateSessionOverTestTransport(
            id,
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions());
    }

    private static ServerConnectionState CreateReadyConnection(RpcSession session)
    {
        var connection = new ServerConnectionState(
            session,
            new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(),
            CancellationToken.None,
            TimeProvider.System,
            maxConcurrentCalls: 1);
        Ensure(connection.MarkReady(null), "connection ready");
        return connection;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
