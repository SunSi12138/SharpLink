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
    public void ServerRegistrationBuildFailureShouldRollbackPriorMaterializationsInReverse()
    {
        var manifest = new RegistrationRollbackManifest();
        var cleanupEvents = new List<string>();
        var first = new TrackingRegistrationServiceOne(cleanupEvents);
        var second = new TrackingRegistrationServiceTwo(cleanupEvents);
        var provider = new TrackingServiceProvider();
        var transport = new TrackingServerTransport(
            cleanupEvents: cleanupEvents,
            cleanupResource: "listener");
        var builder = CreateServerBuilder(manifest)
            .UseTransport(transport)
            .UseServiceProvider(provider)
            .UseAdmissionControl(static options => options.Global.UseConcurrency(1))
            .ReplaceService<IRegistrationServiceOne>(first)
            .ReplaceService<IRegistrationServiceTwo>(second)
            .ReplaceService<IRegistrationBuildFailure>(
                static _ => new RegistrationBuildFailureService(),
                SharpLinkServiceLifetime.Connection);
        MarkReplacementFrameworkOwned(builder, typeof(IRegistrationServiceOne));
        MarkReplacementFrameworkOwned(builder, typeof(IRegistrationServiceTwo));

        var failure = Capture(() => { _ = builder.Build(); });

        Ensure(Contains(failure, "Connection and Call SharpLink services require an IServiceScopeFactory"),
            "the third ServiceRegistrationDefinition.Build failure must remain primary");
        Ensure(provider.RequestedServices.Contains(typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory)),
            "the failing third registration must reach ServiceRegistrationDefinition.Build");
        Ensure(first.DisposeCount == 1 && second.DisposeCount == 1,
            "each framework-owned materialized ServiceRegistration must release its singleton once");
        EnsureSequence(cleanupEvents, "registration:second", "registration:first", "listener");
        Ensure(provider.DisposeCount == 0, "caller provider registration must remain non-disposing");
        Ensure(transport.DisposeCount == 1,
            "listener must release after prior registrations, admission, caller provider, and RuntimeContext rollback");
    }

    [Test]
    public void ServerFinalConstructionFailureMustNotDisposeCallerOwnedService()
    {
        var manifest = new RegistrationRollbackManifest();
        var transport = new TrackingServerTransport();
        var callerOwnedService = new TrackingRegistrationServiceOne([]);
        var logger = new ThrowingLoggerFactory("Server caller service logger construction failed");

        var failure = Capture(() => CreateServerBuilder(manifest)
                .UseTransport(transport)
                .ReplaceService<IRegistrationServiceOne>(callerOwnedService)
                .UseLoggerFactory(logger)
                .Build());

        Ensure(Contains(failure, "Server caller service logger construction failed"),
            "final Server construction failure must remain primary after a caller-owned registration materializes");
        Ensure(callerOwnedService.DisposeCount == 0,
            "rollback must dispose the registration but never the caller-owned service singleton");
        Ensure(logger.DisposeCount == 0, "rollback must not dispose the caller logger factory");
        Ensure(transport.DisposeCount == 1, "rollback must release the framework-owned listener");
    }
}
