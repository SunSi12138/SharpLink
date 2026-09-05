using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task DifferentlyNamedTupleAliasesShouldShareOneCodecGraphIdentity()
    {
        var source = AddAssemblyAttribute(AddAssemblyAttribute(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface ITupleAliasContract : SharpLink.Sdk.IService
{
    ValueTask<(int X, string Y)> A((int X, string Y) value, CancellationToken cancellationToken);
    ValueTask<(int Index, string Label)> B((int Index, string Label) value, CancellationToken cancellationToken);
}

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "tuple.alias/v1";
    public string WireFormatId => "tuple-alias-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"tuple.alias/v1\", \"tuple-alias-wire/v1\")]"),
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(ValueTuple<int, string>), typeof(FakeAdapter))]");

        var generated = RunGeneratorAndGetSources(source);
        var codecs = generated.Single(static text =>
            text.Contains("public Type TargetType => typeof(", StringComparison.Ordinal));

        Ensure(CountOccurrences(codecs, "public Type TargetType => typeof(") == 1,
            "different tuple element names must not create duplicate runtime Codec targets");
        Ensure(codecs.Contains("global::System.ValueTuple", StringComparison.Ordinal),
            "the Codec graph must use the underlying CLR ValueTuple identity");
        Ensure(!codecs.Contains("(int X, string Y)", StringComparison.Ordinal) &&
               !codecs.Contains("(int Index, string Label)", StringComparison.Ordinal),
            "tuple element names are source metadata and must not survive in the Codec factory identity");
        EnsureDoesNotHaveRule(source, "SHARPLINK009");
        return Task.CompletedTask;
    }
}
