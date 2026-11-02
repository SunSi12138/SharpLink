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
            AssertExpectedMaterializationRollback(owner, transport, logger, failure);
            owner.AcceptBuildFailure();
        }
        catch (Exception failure) { owner.RecordBodyFailure(failure); }
        finally { await owner.FinishAsync(testCancellationToken); }
        owner.ThrowIfFailed();
    }

    private static void AssertExpectedMaterializationRollback(RollbackBuildFixture owner,
        RollbackBuildTransport transport, RollbackBuildLogger logger, Exception failure)
    {
        var receipts = owner.Manifest.GetScopeCleanupReceipts();
        Ensure(receipts.Length == 1 && receipts[0].Count == 1 && receipts[0].Order == 1,
            "materialization rollback must dispose exactly its one actual adapter scope");
        Ensure(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 3 &&
            ReferenceEquals(aggregate.InnerExceptions[0], logger.Failure) &&
            ReferenceEquals(aggregate.InnerExceptions[1], receipts[0].Failure) &&
            ReferenceEquals(aggregate.InnerExceptions[2], transport.CleanupFailure),
            "rollback must retain exact primary, scope and listener failures in transaction order");
    }

    [Test]
    [NotInParallel("rollback-plugin")]
    public async Task UnexpectedSuccessfulRollbackBuildShouldRetainEndpointAndJoinItsActualDisposal(
        CancellationToken testCancellationToken)
    {
        var owner = new RollbackBuildFixture();
        RollbackBuildTransport? transport = null;
        Exception? marker = null;
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            owner.Initialize("builder-rollback-unexpected-success");
            Ensure(owner.Manifest.Codecs.Count == 1, "unexpected-success control requires its real codec owner");
            transport = owner.CreateTransport();
            marker = owner.CaptureBuild(CreateServerBuilder(owner.Manifest).UseTransport(transport));
            Ensure(owner.UnexpectedServer is { LifecycleState: SharpLinkServerLifecycleState.Created },
                "the real Build must return an owned, never-started Server before the failure assertion");
            var receipts = owner.Manifest.GetScopeCleanupReceipts();
            Ensure(receipts.Length == 1 && receipts[0].Count == 0 && receipts[0].Order == 0,
                "the returned Server must own one undisposed real generated scope");
            testCancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception failure) { owner.RecordBodyFailure(failure); }
        finally { await owner.FinishAsync(testCancellationToken); }

        var failureGraph = Capture(owner.ThrowIfFailed);
        try
        {
            var finalReceipts = owner.Manifest.GetScopeCleanupReceipts();
            Ensure(finalReceipts.Length == 1 && finalReceipts[0].Count == 1 && finalReceipts[0].Order == 1,
                "unexpected Server disposal must release exactly its original scope once");
            var expectedScope = finalReceipts[0].Failure;
            Ensure(failureGraph is AggregateException final && final.InnerExceptions.Count == 3 &&
                ReferenceEquals(final.InnerExceptions[0], marker) &&
                ReferenceEquals(final.InnerExceptions[1], expectedScope) &&
                final.InnerExceptions[2] is AggregateException sourceEnvelope && sourceEnvelope.InnerExceptions.Count == 1 &&
                ReferenceEquals(sourceEnvelope.InnerExceptions[0], expectedScope),
                "unexpected success must retain exactly marker, actual disposal waiter cause, and full original Task envelope");
            Ensure(owner.UnexpectedServerDisposal is { IsFaulted: true, Exception: { } original } &&
                original.InnerExceptions.Count == 1 && ReferenceEquals(original.InnerExceptions[0], expectedScope) &&
                owner.ServerDisposalCalls == 1 && owner.ServerDisposalOutcomeObserved,
                "the exact returned endpoint Dispose source must be invoked once and actually consumed");
            Ensure(owner.UnexpectedServer!.LifecycleState == SharpLinkServerLifecycleState.Faulted &&
                transport!.DisposeCount == 1 && owner.IsolationReleased && !owner.IsQuarantined,
                "terminal real endpoint ownership, not a wrapper, permits the single isolation release");
            Ensure(Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA") == owner.OriginalSchema &&
                RollbackState.ScopeDisposeCount == owner.OriginalScopeCount,
                "only actual joined cleanup permits exact fixture global restoration");
        }
        catch (Exception validationFailure) { throw new AggregateException(validationFailure, failureGraph); }
    }

    [Test]
    [NotInParallel("rollback-plugin")]
    public async Task RollbackBuildShouldRejectAndPreserveAnIndependentTransportCleanupSibling(
        CancellationToken testCancellationToken)
    {
        var owner = new RollbackBuildFixture();
        var extra = new InvalidOperationException("independent listener cleanup sibling");
        RollbackBuildTransport? transport = null;
        RollbackBuildLogger? logger = null;
        Exception? buildFailure = null;
        Exception? rejection = null;
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            owner.Initialize("builder-rollback-independent-sibling");
            transport = owner.CreateTransport("Server transport cleanup failed", independentCleanupFailure: extra);
            logger = owner.CreateLogger("Server logger construction failed");
            buildFailure = owner.CaptureBuild(CreateServerBuilder(owner.Manifest)
                .UseTransport(transport).UseLoggerFactory(logger));
            Ensure(logger.DisposeCount == 0, "framework rollback must not dispose the caller logger");
            try { AssertExpectedMaterializationRollback(owner, transport, logger, buildFailure); }
            catch (Exception failure) { rejection = failure; owner.RecordBodyFailure(failure); }
            // Never acknowledge the augmented Build graph as the original expected failure.
        }
        catch (Exception failure) { owner.RecordBodyFailure(failure); }
        finally { await owner.FinishAsync(testCancellationToken); }

        var finalFailure = Capture(owner.ThrowIfFailed);
        try
        {
            var receipts = owner.Manifest.GetScopeCleanupReceipts();
            Ensure(rejection is not null && rejection.GetType() == typeof(Exception) && rejection.InnerException is null &&
                rejection.Message == "rollback must retain exact primary, scope and listener failures in transaction order" &&
                finalFailure is AggregateException final && final.InnerExceptions.Count == 2 &&
                ReferenceEquals(final.InnerExceptions[0], rejection) && ReferenceEquals(final.InnerExceptions[1], buildFailure),
                "positive shape rejection must remain first beside the entire unaccepted original Build graph");
            Ensure(receipts.Length == 1 && receipts[0].Count == 1 && receipts[0].Order == 1 &&
                buildFailure is AggregateException build && build.InnerExceptions.Count == 3 &&
                ReferenceEquals(build.InnerExceptions[0], logger!.Failure) &&
                ReferenceEquals(build.InnerExceptions[1], receipts[0].Failure) &&
                build.InnerExceptions[2] is AggregateException listener && listener.InnerExceptions.Count == 2 &&
                ReferenceEquals(listener.InnerExceptions[0], transport!.CleanupFailure) &&
                ReferenceEquals(listener.InnerExceptions[1], extra),
                "the actual listener Dispose must retain both ordered independent siblings without flattening");
            Ensure(transport!.DisposeCount == 1 && logger!.DisposeCount == 1 &&
                owner.UnexpectedServer is null && owner.ServerDisposalCalls == 0 &&
                owner.IsolationReleased && !owner.IsQuarantined,
                "synchronous rollback owns the listener once; the fixture independently owns the caller logger");
            Ensure(Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA") == owner.OriginalSchema &&
                RollbackState.ScopeDisposeCount == owner.OriginalScopeCount,
                "the completed rejected cleanup graph must not strand the fixture globals or permit");
        }
        catch (Exception validationFailure) { throw new AggregateException(validationFailure, finalFailure); }
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
