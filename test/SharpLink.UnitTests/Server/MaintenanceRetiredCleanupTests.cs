using System.Net;
using System.Reflection;
using SharpLink.Server;
using static SharpLink.UnitTests.Server.ServerCallbackOwnershipFixture;

namespace SharpLink.UnitTests.Server;

public sealed class MaintenanceRetiredCleanupTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public Task CompletedRetiredCleanupFailureRemainsVisibleAfterOwnerRemoval(bool fails, bool configuredBeforeCleanup)
        => LongRunningTestWorker.Run(() =>
        {
            var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableAutomaticServiceRegistration().UseTransport(new IdleListener()).Build();
            if (configuredBeforeCleanup)
            {
                server.EnableCooperativeMaintenance(Guid.NewGuid(), [new(Guid.NewGuid(), Guid.NewGuid(), "subject")]);
                LongRunningTestWorker.WaitForPhase(server.StartAsync().AsTask(), Bound);
            }
            using var fixture = new ServerCallbackOwnershipFixture();
            var registry = Field<ServerConnectionRegistry>(server, "_connectionRegistry");
            var catalog = Field<ServerServiceModuleRegistry>(server, "_serviceModuleRegistry");
            fixture.Payload.ThrowOnDispose = fails;
            fixture.Trigger(timer: false);
            fixture.ReleaseInvocation();
            LongRunningTestWorker.WaitForPhase(fixture.Connection.CloseAsync().AsTask(), Bound);
            Ensure(registry.TryRetire(fixture.Connection), "retain physical cleanup ownership");
            var completion = (Task)typeof(SharpLinkServer).GetMethod("CompleteRetiredConnectionCleanupAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, [fixture.Connection])!;
            lock (catalog.Gate)
            {
                fixture.CallbackRelease.Set();
                LongRunningTestWorker.Join(fixture.Connection.ServiceCleanupTask, Bound);
                if (fails)
                    Ensure(registry.SnapshotOwned().Contains(fixture.Connection),
                        "failed owner cannot disappear before sticky publication under the registry gate");
            }
            LongRunningTestWorker.Join(completion, Bound);
            Ensure(completion.IsFaulted == fails && registry.SnapshotOwned().Length == 0,
                "completed cleanup leaves the retired registry without erasing its outcome");
            _ = completion.Exception;
            lock (catalog.Gate)
                Ensure(catalog.HasRetirementFailure == fails, "cold failure witness reflects physical cleanup only");
            var rejected = false;
            try
            {
                if (configuredBeforeCleanup) server.ValidateMaintenanceCohort([]);
                else server.EnableCooperativeMaintenance(Guid.NewGuid(), [new(Guid.NewGuid(), Guid.NewGuid(), "subject")]);
            }
            catch (InvalidOperationException) { rejected = true; }
            Ensure(rejected == fails, "opt-in and future proof checks reject completed cleanup failure, not successful retirement");
            LongRunningTestWorker.WaitForPhase(server.DisposeAsync().AsTask(), Bound);
        });

    private sealed class IdleListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
