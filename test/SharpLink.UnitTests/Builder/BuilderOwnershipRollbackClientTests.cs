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
    public void ClientProfileFailureShouldDisposeTransportAndPreserveBothFailures()
    {
        var transport = new TrackingClientTransport(
            bindingFailure: "direct Client profile binding failed",
            cleanupFailure: "direct Client transport cleanup failed");

        var failure = Capture(() => CreateClientBuilder()
            .UseTransport(transport)
            .Build());

        Ensure(Contains(failure, "direct Client profile binding failed"),
            "direct Client build retains profile failure");
        Ensure(Contains(failure, "direct Client transport cleanup failed"),
            "direct Client build retains transport cleanup failure");
        Ensure(transport.DisposeCount == 1, "direct Client build disposes its transport once");
    }

    [Test]
    public void ClientFinalMaterializationFailureShouldDisposeTransportAndPreserveBothFailures()
    {
        var transport = new TrackingClientTransport(
            bindingFailure: null,
            cleanupFailure: "direct Client construction transport cleanup failed");
        var logger = new ThrowingLoggerFactory("direct Client logger construction failed");

        var failure = Capture(() => CreateClientBuilder()
            .UseTransport(transport)
            .UseLoggerFactory(logger)
            .Build());

        Ensure(Contains(failure, "direct Client logger construction failed"),
            "Client build retains final materialization failure");
        Ensure(Contains(failure, "direct Client construction transport cleanup failed"),
            "direct Client construction retains transport cleanup failure");
        Ensure(transport.DisposeCount == 1, "failed direct Client construction disposes its transport once");
        Ensure(logger.DisposeCount == 0, "Client build failure must not dispose the caller-owned logger factory");
    }

    [Test]
    public void ClientRuntimeContextConstructionFailureShouldRollbackTheConsumedTransport()
    {
        var transport = new TrackingClientTransport(
            bindingFailure: null,
            cleanupFailure: "Client context construction transport cleanup failed");

        var builder = CreateClientBuilder().UseTransport(transport);
        var plan = builder.CompileForMultiCluster([new ThrowingRuntimeContextManifest()]);

        var failure = Capture(() => builder.MaterializeCompiledPlan(plan));

        Ensure(Contains(failure, "controlled Runtime Context construction failure"),
            "Client RuntimeContext construction failure must remain primary");
        Ensure(Contains(failure, "Client context construction transport cleanup failed"),
            "Client RuntimeContext construction failure must aggregate consumed transport cleanup");
        Ensure(transport.DisposeCount == 1, "Client RuntimeContext construction failure disposes transport once");
    }

    [Test]
    public void EndpointFactoryFailureShouldRollbackPreviouslyMaterializedFactories()
    {
        var first = new TrackingClientTransport(
            bindingFailure: null,
            cleanupFailure: "first endpoint factory cleanup failed");

        var failure = Capture(() => CreateClientBuilder()
            .UseEndpoints(
                [CreateEndpoint("first", 6811), CreateEndpoint("second", 6812)],
                endpoint => endpoint.Id == "first"
                    ? first
                    : throw new InvalidOperationException("second endpoint factory failed"))
            .Build());

        Ensure(Contains(failure, "second endpoint factory failed"),
            "endpoint factory exception must remain primary");
        Ensure(Contains(failure, "first endpoint factory cleanup failed"),
            "endpoint factory exception must aggregate previous factory cleanup");
        Ensure(first.DisposeCount == 1, "previous endpoint factory must be disposed exactly once");
    }

    [Test]
    public void StaticClientFactoryBindingFailureShouldRollbackFactoriesInReverseExactlyOnce()
    {
        var probe = new BuilderFaultInjectionProbe();
        var failure = Capture(() => CreateClientBuilder()
            .UseEndpoints(
                [CreateEndpoint("first", 6801), CreateEndpoint("second", 6802)],
                endpoint =>
                {
                    probe.RecordAcquisition(endpoint.Id);
                    return new TrackingClientTransport(
                        bindingFailure: endpoint.Id == "second" ? "second factory binding failed" : null,
                        cleanupFailure: $"{endpoint.Id} factory cleanup failed",
                        probe,
                        endpoint.Id);
                })
            .Build());

        BuilderFaultInjectionProbe.AssertFailureOrder(
            failure,
            "second factory binding failed",
            "second factory cleanup failed",
            "first factory cleanup failed");
        probe.AssertAcquisitionOrder("first", "second");
        probe.AssertReverseCleanupAndExactlyOnce();
    }

    [Test]
    public void DynamicResolverValidationFailureShouldDisposeResolverAndPreserveBothFailures()
    {
        var resolver = new TrackingResolver("dynamic resolver cleanup failed");

        var failure = Capture(() => CreateClientBuilder()
            .UseEndpointResolver(resolver, static _ => new NoopClientTransport())
            .UseConnectionPool(static _ => { })
            .Build());

        Ensure(Contains(failure, "UseConnectionPool is only available"),
            "dynamic Client build retains validation failure");
        Ensure(Contains(failure, "dynamic resolver cleanup failed"),
            "dynamic Client build retains resolver cleanup failure");
        Ensure(resolver.DisposeCount == 1, "failed dynamic Client build disposes its resolver once");
    }

    [Test]
    public void MultiClusterConstructionFailureShouldRollbackCompletedChildren()
    {
        var childTransport = new TrackingClientTransport(
            bindingFailure: null,
            cleanupFailure: "multi-cluster child transport cleanup failed");
        var logger = new MultiClusterThrowingLoggerFactory("multi-cluster logger construction failed");
        var builder = CreateMultiClusterBuilder()
            .AddCluster("dynamic", child => child.UseTransport(childTransport),
                slot => slot.AllowDynamicContracts = true);
        builder.UseLoggerFactoryIfUnset(logger);

        var failure = Capture(() => { _ = builder.Build(); });

        Ensure(Contains(failure, "multi-cluster logger construction failed"),
            "coordinator construction failure must remain primary");
        Ensure(Contains(failure, "multi-cluster child transport cleanup failed"),
            "coordinator construction failure must aggregate completed-child cleanup");
        Ensure(childTransport.DisposeCount == 1, "completed multi-cluster child must be disposed once");
        Ensure(logger.DisposeCount == 0, "MultiCluster build failure must not dispose the caller logger factory");
    }
}
