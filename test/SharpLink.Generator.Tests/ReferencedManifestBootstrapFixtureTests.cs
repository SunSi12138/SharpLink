using System;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ReferencedManifestBootstrapGeneratedOutputShouldMatchFixture()
    {
        var infrastructure = CreateManifestInfrastructureReference();
        var alpha = CreateGeneratedManifestReference(
            "AlphaServices",
            "AlphaManifest",
            "HiddenAlphaService",
            infrastructure);

        GeneratedSourceFixture.AssertGeneratedSource(
            "referenced-manifest-bootstrap",
            "Issue359_referenced-manifest-bootstrap",
            GeneratedSourceFixture.ReadInput("referenced-manifest-bootstrap"),
            "SharpLink.GeneratedReferencedAssemblyBootstrap.g.cs",
            infrastructure,
            alpha);
        return Task.CompletedTask;
    }

    [Test]
    [Arguments(4, 3, "sharplink-3.0-api5-contract-channel-complete-method-facts-v1")]
    [Arguments(6, 3, "sharplink-3.0-api5-contract-channel-complete-method-facts-v1")]
    [Arguments(5, 2, "sharplink-3.0-api5-contract-channel-complete-method-facts-v1")]
    [Arguments(5, 3, "SHARPLINK-3.0-api5-contract-channel-complete-method-facts-v1")]
    public Task ReferencedManifestBootstrapMustRejectIncompatibleIdentity(
        int apiVersion, int protocolVersion, string abiIdentity)
    {
        var infrastructure = CreateManifestInfrastructureReference();
        var current = CreateGeneratedManifestReference(
            "CurrentServices", "CurrentManifest", "HiddenCurrentService", infrastructure);
        var incompatible = CreateGeneratedManifestReference(
            "IncompatibleServices", "IncompatibleManifest", "HiddenIncompatibleService",
            infrastructure, apiVersion, protocolVersion, abiIdentity);
        const string consumer = "namespace Consumer { internal sealed class Marker; }";
        var generated = GetReferencedManifestBootstrap(
            RunGeneratorAndGetSources(consumer, infrastructure, current, incompatible));
        Ensure(CountOccurrences(generated, ".Register();") == 1 &&
               generated.Contains("global::SharpLink.Generated.CurrentManifest.Register();", StringComparison.Ordinal),
            "the API5/protocol3/exact-ABI current reference must still bootstrap");
        Ensure(!generated.Contains("IncompatibleManifest", StringComparison.Ordinal),
            "an otherwise-valid reference with wrong API, protocol or ordinal ABI must not bootstrap");
        EnsureGeneratorOutputCompiles(consumer, infrastructure, current, incompatible);
        return Task.CompletedTask;
    }

}
