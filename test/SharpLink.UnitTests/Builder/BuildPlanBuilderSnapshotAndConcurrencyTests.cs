using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Threading;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests.Builder;

public sealed partial class BuildPlanBuilderTests
{
    [Test]
    public void ClientAndServerBuildersShouldStayConsumedAfterMaterializeFailure()
    {
        var clientTransport = new ProfileFailureClientTransport();
        var clientBuilder = CreateClientBuilder().UseTransport(clientTransport);

        var clientFailure = Capture(() => _ = clientBuilder.Build());

        Ensure(clientFailure is InvalidOperationException && clientFailure.Message == "phase11 Client profile failure",
            "Client Materialize must retain its primary failure");
        Ensure(clientTransport.DisposeCount == 1,
            "Client Materialize rollback must dispose the configured transport exactly once");
        EnsureConsumed(() => _ = clientBuilder.Build());
        EnsureConsumed(() => clientBuilder.UseRequestTimeout(TimeSpan.FromSeconds(1)));

        var serverTransport = new ProfileFailureServerListener();
        var serverBuilder = CreateServerBuilder().UseTransport(serverTransport);

        var serverFailure = Capture(() => _ = serverBuilder.Build());

        Ensure(serverFailure is InvalidOperationException && serverFailure.Message == "phase11 Server profile failure",
            "Server Materialize must retain its primary failure");
        Ensure(serverTransport.DisposeCount == 1,
            "Server Materialize rollback must dispose the configured listener exactly once");
        EnsureConsumed(() => _ = serverBuilder.Build());
        EnsureConsumed(() => serverBuilder.UseHeartbeat(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
    }

    [Test]
    public async Task ClientBuildAndConfigurationRaceShouldHaveOneWinnerAndOneStableConsumedFailure()
    {
        var transport = new BlockingClientTransport();
        var builder = CreateClientBuilder()
            .UseTransport(transport)
            .UseProtocol(static options => options.MaxFramePayloadBytes = 2_048);

        var build = LongRunningTestWorker.Run(builder.Build);
        ISharpLinkClient? client = null;
        try
        {
            Ensure(transport.ProfileBindingEntered.Wait(TimeSpan.FromSeconds(2)),
                "the first Build must reach deterministic materialization coordination");

            EnsureConsumed(() => _ = builder.Build());
            EnsureConsumed(() => builder.UseProtocol(static options => options.MaxFramePayloadBytes = 4_096));

            transport.ReleaseProfileBinding();
            client = await build.WaitAsync(TimeSpan.FromSeconds(2));
            var context = (SharpLinkRuntimeContext)((IRpcChannel)client).RuntimeContext;
            Ensure(context.Protocol.MaxFramePayloadBytes == 2_048,
                "a rejected concurrent configuration must not alter the frozen Client plan");
        }
        finally
        {
            transport.ReleaseProfileBinding();
            client ??= await build.WaitAsync(TimeSpan.FromSeconds(5));
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task ServerBuildAndConfigurationRaceShouldHaveOneWinnerAndOneStableConsumedFailure()
    {
        var listener = new BlockingServerListener();
        var builder = CreateServerBuilder()
            .UseTransport(listener)
            .UseProtocol(static options => options.MaxFramePayloadBytes = 2_048);

        var build = LongRunningTestWorker.Run(builder.Build);
        ISharpLinkServer? server = null;
        try
        {
            Ensure(listener.ProfileBindingEntered.Wait(TimeSpan.FromSeconds(2)),
                "the first Server Build must reach deterministic materialization coordination");

            EnsureConsumed(() => _ = builder.Build());
            EnsureConsumed(() => builder.UseProtocol(static options => options.MaxFramePayloadBytes = 4_096));

            listener.ReleaseProfileBinding();
            server = await build.WaitAsync(TimeSpan.FromSeconds(2));
            var context = ReadPrivate<SharpLinkRuntimeContext>(server, "_runtimeContext");
            Ensure(context.Protocol.MaxFramePayloadBytes == 2_048,
                "a rejected concurrent configuration must not alter the frozen Server plan");
        }
        finally
        {
            listener.ReleaseProfileBinding();
            server ??= await build.WaitAsync(TimeSpan.FromSeconds(5));
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task ClientCompilePlanShouldEnumerateOnceFreezeInputsAndDeferEndpointFactoryCreation()
    {
        var attributes = new Dictionary<string, string> { ["zone"] = "before" };
        var endpoints = new List<SharpLinkEndpoint>
        {
            new()
            {
                Id = "before",
                Address = new SharpLinkTcpAddress("127.0.0.1", 5201),
                Attributes = attributes
            }
        };
        var source = new CountingEndpointEnumerable(endpoints);
        var factoryCalls = 0;
        SharpLinkEndpoint? materializedEndpoint = null;
        var builder = CreateClientBuilder().UseEndpoints(source, endpoint =>
        {
            factoryCalls++;
            materializedEndpoint = endpoint;
            return new TrackingClientTransport();
        });

        var plan = builder.CompileForMultiCluster([]);

        Ensure(source.EnumerationCount == 1 && source.MoveNextCount == 2,
            "Compile must take one complete static endpoint snapshot");
        Ensure(factoryCalls == 0,
            "Compile must not create a framework-owned endpoint transport factory");

        attributes["zone"] = "after";
        endpoints[0] = Endpoint("after", 5202);
        await using var client = builder.MaterializeCompiledPlan(plan);

        Ensure(factoryCalls == 1 && materializedEndpoint is { Id: "before" } &&
               materializedEndpoint.Attributes["zone"] == "before",
            "Materialize must use the frozen endpoint and attributes from the same ClientBuildPlan");
        Ensure(source.EnumerationCount == 1,
            "Materialize must not re-enumerate the source captured by Compile");
    }

    [Test]
    public void EndpointEnumerationFailureShouldConsumeTheBuilderWithoutAcquiringAFactory()
    {
        var source = new ThrowingEndpointEnumerable();
        var factoryCalls = 0;
        var builder = CreateClientBuilder().UseEndpoints(source, _ =>
        {
            factoryCalls++;
            return new TrackingClientTransport();
        });

        var failure = Capture(() => _ = builder.Build());

        Ensure(failure is InvalidOperationException && failure.Message == "endpoint enumeration failed",
            "a mid-enumeration failure must be reported from Compile");
        Ensure(source.EnumerationCount == 1 && source.MoveNextCount == 2 && factoryCalls == 0,
            "a failed static snapshot must not restart enumeration or acquire endpoint factories");
        EnsureConsumed(() => _ = builder.Build());
        EnsureConsumed(() => builder.UseEndpoints([Endpoint("other", 5203)], static _ => new TrackingClientTransport()));
    }

    [Test]
    public async Task ManifestInputShouldBeSnapshottedBeforeMaterialize()
    {
        var manifests = new CountingManifestList([new EmptyManifest()]);
        var builder = CreateClientBuilder().UseTransport(new TrackingClientTransport());

        var plan = builder.CompileForMultiCluster(manifests);
        var accessesAfterCompile = manifests.AccessCount;
        Ensure(accessesAfterCompile == 2,
            "Compile must read the caller manifest list exactly once to create its strong snapshot");
        manifests.RejectFurtherAccess = true;

        await using var client = builder.MaterializeCompiledPlan(plan);

        Ensure(manifests.AccessCount == accessesAfterCompile,
            "Runtime materialization must use the frozen manifest source instead of caller list access");
    }

    [Test]
    public async Task ServerAdmissionOptionsShouldFreezeBeforeMaterialize()
    {
        var listener = new BlockingServerListener();
        SharpLinkConcurrencyLimitOptions? capturedLimit = null;
        var builder = CreateServerBuilder()
            .UseTransport(listener)
            .UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                capturedLimit = options.Global.Concurrency;
            });

        var build = LongRunningTestWorker.Run(builder.Build);
        SharpLinkServer? server = null;
        try
        {
            Ensure(listener.ProfileBindingEntered.Wait(TimeSpan.FromSeconds(2)),
                "the Server Build must have completed Compile before the admission mutation");
            capturedLimit!.PermitLimit = 2;
            listener.ReleaseProfileBinding();

            server = (SharpLinkServer)await build.WaitAsync(TimeSpan.FromSeconds(2));
            var controller = ReadPrivate<SharpLinkAdmissionController>(server, "_admissionController");
            var context = new SharpLinkAdmissionContext(
                contractId: 1,
                methodId: 1,
                methodKind: RpcMethodKind.Unary,
                connectionId: "phase11-admission",
                authenticationContext: null,
                metadata: null);
            var first = await controller.AcquireAsync(
                context, retainedBytes: 1, allowQueue: false, CancellationToken.None);
            var second = await controller.AcquireAsync(
                context, retainedBytes: 1, allowQueue: false, CancellationToken.None);
            try
            {
                Ensure(first.IsAcquired && !second.IsAcquired && second.Reason == "concurrency",
                    "post-Compile mutation of admission options must not alter the frozen permit limit");
            }
            finally
            {
                first.Lease?.Dispose();
                second.Lease?.Dispose();
            }
        }
        finally
        {
            listener.ReleaseProfileBinding();
            server ??= (SharpLinkServer)await build.WaitAsync(TimeSpan.FromSeconds(5));
            await server.DisposeAsync();
        }
    }
}
