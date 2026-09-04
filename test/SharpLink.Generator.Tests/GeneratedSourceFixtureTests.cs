using System;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ContractAndServiceGeneratedOutputShouldMatchFixture()
    {
        var source = BuildSource(GeneratedSourceFixture.ReadInput("contract-service"));
        GeneratedSourceFixture.AssertGeneratedSource(
            "contract-service",
            "Issue353_contract-service",
            source,
            "SharpLink.ContractManifest.g.cs");
        return Task.CompletedTask;
    }

    [Test]
    public Task DtoCodecGeneratedOutputShouldMatchFixture()
    {
        var source = BuildSource(GeneratedSourceFixture.ReadInput("dto-codec"));
        GeneratedSourceFixture.AssertGeneratedSource(
            "dto-codec",
            "Issue353_dto-codec",
            source,
            "SharpLink.GeneratedCodecs.g.cs");
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedSourceFixtureDiffShouldShowExpectedAndActualLines()
    {
        var diff = GeneratedSourceFixture.BuildReadableDiff(
            "sample",
            "Sample.g.cs",
            "first\nexpected\nlast\n",
            "first\nactual\nlast\n");
        Ensure(diff.Contains("--- expected", StringComparison.Ordinal), "fixture diff expected header");
        Ensure(diff.Contains("+++ actual", StringComparison.Ordinal), "fixture diff actual header");
        Ensure(diff.Contains("- 2 | expected", StringComparison.Ordinal), "fixture diff removed line");
        Ensure(diff.Contains("+ 2 | actual", StringComparison.Ordinal), "fixture diff added line");
        return Task.CompletedTask;
    }
}
