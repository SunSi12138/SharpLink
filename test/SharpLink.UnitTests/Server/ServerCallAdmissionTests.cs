using System.IO.Pipelines;
using System.Reflection;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerCallAdmissionTests
{
    [Test]
    public async Task ServerCapacityFailureShouldRollbackLocalAndProvisionalGlobalOwnership()
    {
        await using var server = CreateServer(
            maxConcurrentCallsPerConnection: 1,
            maxConcurrentCallsPerServer: 1);
        await using var firstSession = CreateSession("admission-first");
        await using var secondSession = CreateSession("admission-second");
        var firstConnection = CreateConnection(firstSession);
        var secondConnection = CreateConnection(secondSession);
        Ensure(firstConnection.MarkReady(null), "first connection ready");
        Ensure(secondConnection.MarkReady(null), "second connection ready");

        var first = server.TryAcquireCall(firstConnection);
        Ensure(first == ServerCallAdmissionResult.Acquired,
            "first call must acquire the only server slot");

        var rejected = server.TryAcquireCall(secondConnection);
        Ensure(rejected == ServerCallAdmissionResult.ServerCapacityExhausted,
            "second connection must fail at the server-wide capacity boundary");
        Ensure(server.ActiveCallCountForDiagnostics == 1 &&
               server.PendingCallAdmissionsForDiagnostics == 0,
            "failed global admission must retain only the first owned server slot");
        Ensure(firstConnection.ActiveCalls == 1 && secondConnection.ActiveCalls == 0,
            "failed global admission must roll back the second connection's local slot");

        server.ReleaseCall(firstConnection);
        Ensure(server.ActiveCallCountForDiagnostics == 0 && firstConnection.ActiveCalls == 0,
            "releasing the surviving owner must make all capacity reusable");
    }

#if DEBUG
    [Test]
    public async Task LifecycleChangeAfterCapacityAcquisitionShouldRollbackBothScopes()
    {
        await using var server = CreateServer(
            maxConcurrentCallsPerConnection: 1,
            maxConcurrentCallsPerServer: 1);
        await using var session = CreateSession("admission-stop-race");
        var connection = CreateConnection(
            session,
            afterLocalCallAdmission: () => SetServerState(server, draining: true));
        Ensure(connection.MarkReady(null), "connection ready");

        var result = server.TryAcquireCall(connection);

        Ensure(result == ServerCallAdmissionResult.Unavailable,
            "a lifecycle change after local capacity acquisition must reject the call");
        Ensure(server.ActiveCallCountForDiagnostics == 0 &&
               server.PendingCallAdmissionsForDiagnostics == 0,
            "rejected admission must fully roll back global and pending ownership");
        Ensure(connection.ActiveCalls == 0,
            "rejected admission must roll back the connection-local slot before returning");
        Ensure(server.CallsDrainedForDiagnostics.IsCompletedSuccessfully,
            "rollback completion must notify the server-owned drain coordinator");
    }
#endif

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

    private static ServerConnectionState CreateConnection(RpcSession session)
        => new(
            session,
            new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(),
            CancellationToken.None,
            TimeProvider.System,
            maxConcurrentCalls: 1);

#if DEBUG
    private static ServerConnectionState CreateConnection(
        RpcSession session,
        Action afterLocalCallAdmission)
        => new(
            session,
            new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(),
            CancellationToken.None,
            TimeProvider.System,
            maxConcurrentCalls: 1,
            afterLocalCallAdmission: afterLocalCallAdmission);
#endif

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
