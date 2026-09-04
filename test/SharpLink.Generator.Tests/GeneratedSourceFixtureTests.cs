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

    [Test]
    public Task GeneratedSourceFixtureDiffShouldResyncAfterSingleLineInsertionAndDeletion()
    {
        var insertion = GeneratedSourceFixture.BuildReadableDiff(
            "sample",
            "Sample.g.cs",
            "first\nsecond\nthird\nlast\n",
            "first\ninserted\nsecond\nthird\nlast\n");
        Ensure(insertion.Contains("+ 2 | inserted", StringComparison.Ordinal), "fixture diff inserted line");
        Ensure(insertion.Contains("  2/3 | second", StringComparison.Ordinal), "fixture diff insertion suffix alignment");
        Ensure(!insertion.Contains("- 2 | second", StringComparison.Ordinal),
            "fixture diff must not report stable insertion suffix as removed");

        var deletion = GeneratedSourceFixture.BuildReadableDiff(
            "sample",
            "Sample.g.cs",
            "first\nremoved\nsecond\nthird\nlast\n",
            "first\nsecond\nthird\nlast\n");
        Ensure(deletion.Contains("- 2 | removed", StringComparison.Ordinal), "fixture diff deleted line");
        Ensure(deletion.Contains("  3/2 | second", StringComparison.Ordinal), "fixture diff deletion suffix alignment");
        Ensure(!deletion.Contains("+ 2 | second", StringComparison.Ordinal),
            "fixture diff must not report stable deletion suffix as added");
        return Task.CompletedTask;
    }
}
