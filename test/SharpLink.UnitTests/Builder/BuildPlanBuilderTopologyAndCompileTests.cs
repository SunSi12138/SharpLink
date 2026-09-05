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
    public async Task CrossTopologyConfigurationShouldFailAtTheSecondCall()
    {
        var cases = new[]
        {
            (First: ClientTopology.Fixed, Second: ClientTopology.Static),
            (First: ClientTopology.Static, Second: ClientTopology.Fixed),
            (First: ClientTopology.Fixed, Second: ClientTopology.Dynamic),
            (First: ClientTopology.Dynamic, Second: ClientTopology.Fixed),
            (First: ClientTopology.Static, Second: ClientTopology.Dynamic),
            (First: ClientTopology.Dynamic, Second: ClientTopology.Static)
        };

        foreach (var testCase in cases)
        {
            var builder = CreateClientBuilder();
            ConfigureTopology(builder, testCase.First);

            var failure = Capture(() => ConfigureTopology(builder, testCase.Second));
            Ensure(failure is InvalidOperationException &&
                   failure.Message == "UseTransport, UseEndpoint(s), and UseEndpointResolver are mutually exclusive.",
                $"{testCase.First} -> {testCase.Second} must fail immediately at the second configuration call");

            await using var client = builder.Build();
        }
    }

    [Test]
    public async Task SameTopologyReconfigurationShouldBeRejectedAndDocumentedByBehavior()
    {
        foreach (var topology in new[]
                 {
                     ClientTopology.Fixed,
                     ClientTopology.Static,
                     ClientTopology.Dynamic
                 })
        {
            var builder = CreateClientBuilder();
            ConfigureTopology(builder, topology);

            var failure = Capture(() => ConfigureTopology(builder, topology));
            Ensure(failure is InvalidOperationException &&
                   failure.Message == "A Client topology has already been configured for this builder.",
                $"same-kind {topology} configuration must be rejected instead of replacing a pending owner");

            await using var client = builder.Build();
        }
    }

    [Test]
    public void ClientBuilderShouldStayConsumedAfterCompileFailureAndReleaseItsOwnedTransport()
    {
        var transport = new TrackingClientTransport();
        var builder = CreateClientBuilder()
            .UseTransport(transport)
            .UseProtocol(static options =>
                options.MaxFramePayloadBytes = SharpLinkProtocolOptions.MinMaxFramePayloadBytes - 1);

        var failure = Capture(() => _ = builder.Build());

        Ensure(failure is ArgumentOutOfRangeException,
            "invalid protocol options must fail during Compile");
        Ensure(transport.DisposeCount == 1,
            "a configured direct transport must be released exactly once when Compile fails");
        EnsureConsumed(() => _ = builder.Build());
        EnsureConsumed(() => builder.UseRequestTimeout(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void IncompatibleManifestShouldFailDuringCompileWithoutMaterializingAClientRuntime()
    {
        var transport = new TrackingClientTransport();
        var builder = CreateClientBuilder().UseTransport(transport);

        var failure = Capture(() => _ = builder.CompileForMultiCluster([new IncompatibleManifest()]));

        Ensure(failure is InvalidOperationException && failure.Message.Contains("API", StringComparison.Ordinal),
            "generated-manifest compatibility must fail during pure Compile");
        Ensure(transport.DisposeCount == 1,
            "a Compile-only manifest failure must release the unmaterialized transport once");
        EnsureConsumed(() => _ = builder.Build());
    }

    [Test]
    public void MalformedApi4ManifestShouldFailDuringClientCompileBeforeMaterializingResources()
        => AssertSemanticManifestCompileFailure(new MalformedApi4Manifest(), "malformed API 4 manifest");

    [Test]
    public void ForeignContractOwnershipShouldFailDuringClientCompileBeforeMaterializingResources()
        => AssertSemanticManifestCompileFailure(new ForeignContractOwnershipManifest(), "foreign contract ownership");

    [Test]
    public async Task SemanticManifestValidationShouldDeferCodecAndAdapterMaterialization()
    {
        var adapter = new DeferredAdapter();
        var factory = new DeferredAdapterCodecFactory(adapter);
        var builder = CreateClientBuilder().UseTransport(new TrackingClientTransport());

        var plan = builder.CompileForMultiCluster([new DeferredAdapterManifest(factory)]);

        Ensure(adapter.ScopeCreateCount == 0 && factory.CodecCreateCount == 0,
            "full Compile validation must not create adapter scopes or Codecs");

        await using var client = builder.MaterializeCompiledPlan(plan);

        Ensure(adapter.ScopeCreateCount == 1 && factory.CodecCreateCount == 1,
            "Materialize must create the deferred adapter scope and Codec exactly once");
    }
}
