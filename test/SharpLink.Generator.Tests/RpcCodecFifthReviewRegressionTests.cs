using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task CanonicalTupleAliasBindingsShouldDiagnoseConflictingAdapters()
    {
        var source = AddAssemblyAttributes(UseCurrentIdentitySdk(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IAliasConflictContract : SharpLink.Sdk.IService
{
    ValueTask<List<(int X, int Y)>> Echo(List<(int X, int Y)> value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(1UL, 1UL)]
public sealed class AliasAdapterA : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "alias-a/v1";
    public string WireFormatId => "alias-a-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(2UL, 2UL)]
public sealed class AliasAdapterB : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "alias-b/v1";
    public string WireFormatId => "alias-b-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
""")),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AliasAdapterA), \"alias-a/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AliasAdapterB), \"alias-b/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(List<(int X, int Y)>), typeof(AliasAdapterA))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(List<ValueTuple<int, int>>), typeof(AliasAdapterB))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK045"),
            "different explicit Adapter bindings for one canonical CLR target must report a selection conflict");
        return Task.CompletedTask;
    }

    [Test]
    public Task CanonicalTupleAliasCustomCodecShouldValidateAgainstClrIdentity()
    {
        var source = AddAssemblyAttributes(UseCurrentIdentitySdk(BuildSource("""
[SharpLink.Sdk.RpcCodecSemanticIdentity(3UL, 3UL)]
public sealed class AliasCustomCodec : SharpLink.Abstractions.IRpcCodec<List<(int X, int Y)>>
{
}

[SharpLink.Sdk.RpcContract]
public interface IAliasCustomContract : SharpLink.Sdk.IService
{
    ValueTask<List<(int X, int Y)>> Echo(List<(int X, int Y)> value, CancellationToken cancellationToken);
}
""")),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(List<ValueTuple<int, int>>), typeof(AliasCustomCodec))]");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK060"),
            "custom IRpcCodec<T> validation must use canonical CLR identity for nested tuple aliases");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("new global::AliasCustomCodec()", StringComparison.Ordinal),
            "canonical custom binding must be selected for the tuple-alias payload");
        return Task.CompletedTask;
    }

    [Test]
    public Task CanonicalTupleAliasBindingsShouldDiagnoseConflictingCustomCodecs()
    {
        var source = AddAssemblyAttributes(UseCurrentIdentitySdk(BuildSource("""
[SharpLink.Sdk.RpcCodecSemanticIdentity(4UL, 4UL)]
public sealed class AliasCustomCodecA : SharpLink.Abstractions.IRpcCodec<List<(int X, int Y)>> { }

[SharpLink.Sdk.RpcCodecSemanticIdentity(5UL, 5UL)]
public sealed class AliasCustomCodecB : SharpLink.Abstractions.IRpcCodec<List<ValueTuple<int, int>>> { }

[SharpLink.Sdk.RpcContract]
public interface IAliasCustomConflictContract : SharpLink.Sdk.IService
{
    ValueTask<List<(int X, int Y)>> Echo(List<(int X, int Y)> value, CancellationToken cancellationToken);
}
""")),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(List<(int X, int Y)>), typeof(AliasCustomCodecA))]",
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(List<ValueTuple<int, int>>), typeof(AliasCustomCodecB))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK062"),
            "different custom Codec bindings for one canonical CLR target must report a selection conflict");
        return Task.CompletedTask;
    }
}
