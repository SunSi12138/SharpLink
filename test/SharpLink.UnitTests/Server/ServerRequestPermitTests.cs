using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerRequestPermitTests
{
    [Test]
    public async Task ReservedPermitShouldHoldCapacityAndReleaseExactlyOnce()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseRuntime(options =>
            {
                options.FlowControl.MaxConcurrentCallsPerConnection = 1;
                options.FlowControl.MaxConcurrentCallsPerServer = 1;
            })
            .UseTransport(new IdleListener())
            .Build();
        await using var session = CreateSession("permit-capacity");
        var connection = CreateConnection(session);
        Ensure(connection.MarkReady(null), "connection ready");
        SetServerState(server, 2); // Running

        try
        {
            var admission = server.TryReserveCall(connection, out var permit);
            Ensure(admission == SharpLinkServer.ServerCallAdmissionResult.Acquired && permit is not null,
                "first permit must reserve call capacity");
            Ensure(permit.IsReserved && !permit.IsActive,
                "new permit must start in Reserved state");
            Ensure(server.PendingCallAdmissionsForDiagnostics == 0 &&
                   server.ActiveCallCountForDiagnostics == 1 &&
                   connection.ActiveCalls == 1,
                "Reserved permit must remain visible to the existing drain-safe capacity accounting");

            var rejected = server.TryReserveCall(connection, out var rejectedPermit);
            Ensure(rejected == SharpLinkServer.ServerCallAdmissionResult.PerConnectionCapacityExhausted &&
                   rejectedPermit is null,
                "a Reserved permit must consume the configured connection capacity before activation");

            var alias = permit;
            permit.Activate();
            Ensure(permit.IsActive && !permit.IsReserved,
                "Activate must move the unique permit to Active without changing occupied capacity");
            Ensure(server.ActiveCallCountForDiagnostics == 1 && connection.ActiveCalls == 1,
                "activation must not acquire a second call slot");

            alias.Dispose();
            permit.Dispose();
            Ensure(server.PendingCallAdmissionsForDiagnostics == 0 &&
                   server.ActiveCallCountForDiagnostics == 0 &&
                   connection.ActiveCalls == 0,
                "aliases must release the backing local/global capacity exactly once");

            var recovered = server.TryReserveCall(connection, out var recoveredPermit);
            Ensure(recovered == SharpLinkServer.ServerCallAdmissionResult.Acquired &&
                   recoveredPermit is not null,
                "capacity must be reusable after permit disposal");
            recoveredPermit.Dispose();
            Ensure(server.ActiveCallCountForDiagnostics == 0 && connection.ActiveCalls == 0,
                "disposing a still-Reserved permit must roll capacity back without activation");
        }
        finally
        {
            SetServerState(server, 3); // Draining
            await connection.CloseAsync();
            await connection.ServiceCleanupTask;
        }
    }

    [Test]
    public async Task ReservedPermitShouldKeepServerDrainOpenUntilDisposed()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseRuntime(options =>
            {
                options.FlowControl.MaxConcurrentCallsPerConnection = 1;
                options.FlowControl.MaxConcurrentCallsPerServer = 1;
            })
            .UseTransport(new IdleListener())
            .Build();
        await using var session = CreateSession("permit-drain");
        var connection = CreateConnection(session);
        Ensure(connection.MarkReady(null), "connection ready");
        SetServerState(server, 2); // Running

        var admission = server.TryReserveCall(connection, out var permit);
        Ensure(admission == SharpLinkServer.ServerCallAdmissionResult.Acquired && permit is not null,
            "permit reservation");
        Ensure(permit.IsReserved, "permit must remain Reserved for the drain-boundary probe");

        SetServerState(server, 3); // Draining
        InvokeTrySignalCallsDrained(server);
        Ensure(!server.CallsDrainedForDiagnostics.IsCompleted,
            "drain must not complete while a Reserved permit owns capacity");
        Ensure(server.ActiveCallCountForDiagnostics == 1 && connection.ActiveCalls == 1,
            "Reserved ownership must remain counted across the server drain boundary");

        permit.Dispose();
        await server.CallsDrainedForDiagnostics.WaitAsync(TimeSpan.FromSeconds(1));
        Ensure(server.ActiveCallCountForDiagnostics == 0 && connection.ActiveCalls == 0,
            "disposing the Reserved permit must release both capacity scopes");

        await connection.CloseAsync();
        await connection.ServiceCleanupTask;
    }

    private static RpcSession CreateSession(string id)
    {
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            id,
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions());
        RpcSessionTestFixture.CompleteHandshake(session);
        return session;
    }

    private static ServerConnectionState CreateConnection(RpcSession session)
        => new(
            session,
            new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(),
            CancellationToken.None,
            TimeProvider.System,
            maxConcurrentCalls: 1);

    private static void SetServerState(SharpLinkServer server, int state)
    {
        var field = typeof(SharpLinkServer).GetField(
            "_state",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find server lifecycle state");
        field.SetValue(server, state);
    }

    private static void InvokeTrySignalCallsDrained(SharpLinkServer server)
    {
        var method = typeof(SharpLinkServer).GetMethod(
            "TrySignalCallsDrained",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find call-drain signal path");
        method.Invoke(server, [null]);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new OperationCanceledException(cancellationToken));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
