using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task FinalCodecPlanShouldResolveDirectEnumAndRawNullableWithoutGeneratorFailure()
    {
        var directEnum = BuildSource("""
public enum DirectStatus : byte { Ok = 0, Error = 1 }

[SharpLink.Sdk.RpcContract]
public interface IResolvedEnumContract : SharpLink.Sdk.IService
{
    ValueTask<DirectStatus> Echo(DirectStatus value, CancellationToken cancellationToken);
}
""");
        AssertResolvedManifest(directEnum, "direct enum");

        var rawNullable = BuildSource("""
public enum NullableStatus : int { Ok = 0, Error = 1 }

[SharpLink.Sdk.RpcContract]
public interface IResolvedNullableContract : SharpLink.Sdk.IService
{
    ValueTask<NullableStatus?> Echo(NullableStatus? value, CancellationToken cancellationToken);
}
""");
        AssertResolvedManifest(rawNullable, "raw Nullable<enum>");
        return Task.CompletedTask;
    }

    private static void AssertResolvedManifest(string source, string scenario)
    {
        var diagnostics = RunGenerator(source);
        var generated = RunGeneratorAndGetSources(source);
        Ensure(
            generated.Any(static text => text.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal)),
            $"FinalCodecPlan failed to produce a manifest for {scenario}. Generator diagnostics: {FormatDiagnostics(diagnostics)}");
    }
}
