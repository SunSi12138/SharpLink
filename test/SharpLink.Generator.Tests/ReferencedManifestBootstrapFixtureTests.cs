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
}
