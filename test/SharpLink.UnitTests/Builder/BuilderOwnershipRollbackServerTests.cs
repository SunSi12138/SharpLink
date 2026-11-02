using System.Net;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using SharpLink.Client;
using SharpLink.RollbackPlugin;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Builder;

public partial class BuilderOwnershipRollbackTests
{
    [Test]
    [NotInParallel("rollback-plugin")]
    public async Task ServerCompileValidationFailureShouldNotMaterializeRuntimeContext(CancellationToken testCancellationToken)
    {
        var owner = new RollbackBuildFixture();
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            owner.Initialize("builder-rollback-schema");
            var transport = owner.CreateTransport();
            var builder = CreateServerBuilder(owner.Manifest)
                .UseTransport(transport)
                .EnableService<IMissingService>();
            var failure = owner.CaptureBuild(builder);

            Ensure(Contains(failure, "required contract"), "Server Compile retains service validation failure");
            Ensure(!Contains(failure, "rollback Adapter scope cleanup failed"),
                "Server Compile validation must not create a RuntimeContext cleanup path");
            Ensure(RollbackState.ScopeDisposeCount == 0,
                "Server Compile validation must not materialize generated adapter scopes");
            Ensure(transport.DisposeCount == 1, "Server Compile validation still disposes listener once");
            Ensure(failure.GetType() == typeof(InvalidOperationException) && failure.InnerException is null &&
                failure.Message == $"Generated service for required contract '{typeof(IMissingService).FullName}' was not found." &&
                owner.Manifest.GetScopeCleanupReceipts().Length == 0,
                "Compile failure must be the sole exact required-contract validation outcome");
            owner.AcceptBuildFailure();
        }
        catch (Exception failure) { owner.RecordBodyFailure(failure); }
        finally { await owner.FinishAsync(testCancellationToken); }
        owner.ThrowIfFailed();
    }

    [Test]
    [NotInParallel("rollback-plugin")]
    public async Task ServerFinalMaterializationFailureShouldDisposeRuntimeContextAndPreserveBothFailures(CancellationToken testCancellationToken)
    {
        var owner = new RollbackBuildFixture();
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            owner.Initialize("builder-rollback-schema");
            var transport = owner.CreateTransport("Server transport cleanup failed");
            var logger = owner.CreateLogger("Server logger construction failed");
            var builder = CreateServerBuilder(owner.Manifest)
                .UseTransport(transport)
                .UseLoggerFactory(logger);
            var failure = owner.CaptureBuild(builder);

            Ensure(Contains(failure, "Server logger construction failed"),
                "Server build retains final materialization failure");
            Ensure(Contains(failure, "rollback Adapter scope cleanup failed"),
                "Server final materialization rollback retains Runtime Context cleanup failure");
            Ensure(Contains(failure, "Server transport cleanup failed"),
                "Server final materialization rollback retains transport cleanup failure");
            Ensure(RollbackState.ScopeDisposeCount == 1, "Server final materialization rollback disposes Context once");
            Ensure(transport.DisposeCount == 1, "failed Server build disposes its listener once");
            Ensure(logger.DisposeCount == 0, "Server build failure must not dispose the caller logger factory");
            var receipts = owner.Manifest.GetScopeCleanupReceipts();
            Ensure(receipts.Length == 1 && receipts[0].Count == 1 && receipts[0].Order == 1,
                "materialization rollback must dispose exactly its one actual adapter scope");
            Ensure(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 3 &&
                ReferenceEquals(aggregate.InnerExceptions[0], logger.Failure) &&
                ReferenceEquals(aggregate.InnerExceptions[1], receipts[0].Failure) &&
                ReferenceEquals(aggregate.InnerExceptions[2], transport.CleanupFailure),
                "rollback must retain exact primary, scope and listener failures in transaction order");
            owner.AcceptBuildFailure();
        }
        catch (Exception failure) { owner.RecordBodyFailure(failure); }
        finally { await owner.FinishAsync(testCancellationToken); }
        owner.ThrowIfFailed();
    }

    [Test]
    public async Task ServerListenerShouldBeTransferredByOnlyOneBuild()
    {
        var transport = new TrackingServerTransport();
        var builder = CreateServerBuilder().UseTransport(transport);
        var first = builder.Build();

        var failure = Capture(() => builder.Build());
        Ensure(failure is InvalidOperationException, "a second build must require a replacement listener");

        await first.DisposeAsync();
        Ensure(transport.DisposeCount == 1, "one Server must own and dispose the listener");
    }

    [Test]
    public void ServerRuntimeContextConstructionFailureShouldRollbackTheConsumedListener()
    {
        var manifest = new ThrowingRuntimeContextManifest();
        var transport = new TrackingServerTransport("Server context construction listener cleanup failed");
        var failure = Capture(() => CreateServerBuilder(manifest)
            .UseTransport(transport)
            .Build());

        Ensure(Contains(failure, "controlled Runtime Context construction failure"),
            "Server RuntimeContext construction failure must remain primary");
        Ensure(Contains(failure, "Server context construction listener cleanup failed"),
            "Server RuntimeContext construction failure must aggregate listener cleanup");
        Ensure(transport.DisposeCount == 1, "Server RuntimeContext construction failure disposes listener once");
    }

    [Test]
    public void ServerProfileFailureShouldRollbackListenerAndRuntimeContext()
    {
        var transport = new TrackingServerTransport(
            cleanupFailure: "Server profile listener cleanup failed",
            bindingFailure: "Server listener profile bind failed");

        var failure = Capture(() => CreateServerBuilder()
            .UseTransport(transport)
            .Build());

        Ensure(Contains(failure, "Server listener profile bind failed"),
            "Server listener profile failure must remain primary");
        Ensure(Contains(failure, "Server profile listener cleanup failed"),
            "Server listener profile failure must aggregate listener cleanup");
        Ensure(transport.DisposeCount == 1, "Server listener profile failure disposes listener once");
    }

    [Test]
    public void ServerAdmissionFailureMustRollbackFrameworkResourcesWithoutDisposingCallerProvider()
    {
        var transport = new TrackingServerTransport();
        var provider = new TrackingServiceProvider();

        var failure = Capture(() => CreateServerBuilder()
            .UseTransport(transport)
            .UseServiceProvider(provider)
            .UseAdmissionControl(options => options.AddContract<IMissingService>(
                static rule => rule.UseConcurrency(1)))
            .Build());

        Ensure(Contains(failure, "required by admission control was not found"),
            "admission construction failure must remain primary");
        Ensure(transport.DisposeCount == 1, "admission construction failure disposes listener once");
        Ensure(provider.DisposeCount == 0, "admission failure must not dispose caller-provided service providers");
    }

    [Test]
    public void ServerConstructionFailureMustNotDisposeCallerProvider()
    {
        var transport = new TrackingServerTransport();
        var provider = new TrackingServiceProvider();
        var logger = new ThrowingLoggerFactory("Server caller provider logger construction failed");

        var failure = Capture(() => CreateServerBuilder()
            .UseTransport(transport)
            .UseServiceProvider(provider)
            .UseLoggerFactory(logger)
            .Build());

        Ensure(Contains(failure, "Server caller provider logger construction failed"),
            "Server final construction failure must remain primary");
        Ensure(transport.DisposeCount == 1, "Server final construction failure disposes listener once");
        Ensure(provider.DisposeCount == 0, "Server final construction failure must not dispose caller providers");
        Ensure(logger.DisposeCount == 0, "Server final construction failure must not dispose caller loggers");
    }
}
