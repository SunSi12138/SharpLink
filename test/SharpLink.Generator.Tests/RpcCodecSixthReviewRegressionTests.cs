using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task RuntimeSizedVectorShouldRequireExplicitCodec()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IVectorContract : SharpLink.Sdk.IService
{
    ValueTask<System.Numerics.Vector<int>> Echo(
        System.Numerics.Vector<int> value,
        CancellationToken cancellationToken);
}
""");

        var diagnostics = RunGenerator(source);
        Ensure(
            diagnostics.Any(static diagnostic =>
                diagnostic.GetMessage().Contains("runtime-sized intrinsic unmanaged types", StringComparison.Ordinal)),
            $"Vector<T> must be rejected from the implicit UnsafeBlit path. Diagnostics: {FormatDiagnostics(diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task ContractOnlyCustomCodecSemanticIdentityChangeShouldBreakBaseline()
    {
        static string ContractSource(ulong semanticLow) => AddAssemblyAttribute(
            UseCurrentIdentitySdk(BuildSource($$"""
public sealed class BaselineGraphChild
{
    public int Value { get; set; }
}

public sealed class BaselineGraphParent
{
    public BaselineGraphChild Child { get; set; } = new();
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x5001UL, {{semanticLow}}UL)]
public sealed class BaselineGraphChildCodec : SharpLink.Abstractions.IRpcCodec<BaselineGraphChild>
{
}

[SharpLink.Sdk.RpcContract]
public interface IBaselineGraphContract : SharpLink.Sdk.IService
{
    ValueTask<BaselineGraphParent> Echo(
        BaselineGraphParent value,
        CancellationToken cancellationToken);
}
""")),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(BaselineGraphChild), typeof(BaselineGraphChildCodec))]");

        var baseline = RunContractGenerator(ContractSource(0x6001UL)).Json;
        var baselineRoot = System.Text.Json.Nodes.JsonNode.Parse(baseline)!.AsObject();
        Ensure(
            baselineRoot["codecs"]!.AsArray()
                .Select(static item => item!.AsObject())
                .Any(static item => item["kind"]!.GetValue<string>() == "Custom"),
            "the contract-owned custom Codec must be published in the contract baseline identity graph");

        var changed = RunContractGenerator(ContractSource(0x6002UL), baseline);
        Ensure(
            changed.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "changing a Contract-only custom Codec semantic identity must fail baseline comparison");
        return Task.CompletedTask;
    }
}
