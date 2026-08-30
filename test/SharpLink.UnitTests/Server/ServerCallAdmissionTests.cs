using System.IO.Pipelines;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerCallAdmissionTests
{
    [Test]
    public async Task ServerCapacityFailureShouldRollbackLocalAndProvisionalGlobalOwnership()
    {
        var resourceGovernor = new ServerResourceGovernor(1, 1024, 1024);
        var admission = new ServerCallAdmission(
            maxConcurrentCallsPerConnection: 1,
            maxConcurrentCallsPerServer: 1,
            isServerRunning: static () => true,
            trySignalCallsDrained: static _ => { },
            resourceGovernor: () => resourceGovernor);
        await using var firstSession = CreateSession("admission-first");
        await using var secondSession = CreateSession("admission-second");
        var firstConnection = CreateConnection(firstSession);
        var secondConnection = CreateConnection(secondSession);
        Ensure(firstConnection.MarkReady(null), "first connection ready");
        Ensure(secondConnection.MarkReady(null), "second connection ready");

        var first = admission.TryAcquireCall(firstConnection);
        Ensure(first == SharpLinkServer.ServerCallAdmissionResult.Acquired,
            "first call must acquire the only server slot");

        var rejected = admission.TryAcquireCall(secondConnection);
        Ensure(rejected == SharpLinkServer.ServerCallAdmissionResult.ServerCapacityExhausted,
            "second connection must fail at the server-wide capacity boundary");
        Ensure(admission.ActiveCallCount == 1 && admission.PendingCallAdmissions == 0,
            "failed global admission must retain only the first owned server slot");
        Ensure(firstConnection.ActiveCalls == 1 && secondConnection.ActiveCalls == 0,
            "failed global admission must roll back the second connection's local slot");

        admission.ReleaseCall(firstConnection);
        Ensure(admission.ActiveCallCount == 0 && firstConnection.ActiveCalls == 0,
            "releasing the surviving owner must make all capacity reusable");
    }

    [Test]
    public async Task LifecycleChangeAfterCapacityAcquisitionShouldRollbackBothScopes()
    {
        var runningChecks = 0;
        var drainSignals = 0;
        var resourceGovernor = new ServerResourceGovernor(1, 1024, 1024);
        var admission = new ServerCallAdmission(
            maxConcurrentCallsPerConnection: 1,
            maxConcurrentCallsPerServer: 1,
            isServerRunning: () => Interlocked.Increment(ref runningChecks) < 3,
            trySignalCallsDrained: _ => Interlocked.Increment(ref drainSignals),
            resourceGovernor: () => resourceGovernor);
        await using var session = CreateSession("admission-stop-race");
        var connection = CreateConnection(session);
        Ensure(connection.MarkReady(null), "connection ready");

        var result = admission.TryAcquireCall(connection);

        Ensure(result == SharpLinkServer.ServerCallAdmissionResult.Unavailable,
            "a lifecycle change after capacity acquisition must reject the call");
        Ensure(runningChecks == 3,
            "the test must cross the final lifecycle recheck after local/global acquisition");
        Ensure(admission.ActiveCallCount == 0 && admission.PendingCallAdmissions == 0,
            "rejected admission must fully roll back global and pending ownership");
        Ensure(connection.ActiveCalls == 0,
            "rejected admission must roll back the connection-local slot before returning");
        Ensure(drainSignals >= 1,
            "rollback completion must notify the server-owned drain coordinator");
    }

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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
