using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ContractOnlyCustomChildShouldKeepGlobalCodecGraphClosed()
    {
        var source = AddAssemblyAttribute(BuildSource("""
public sealed class GraphChild
{
    public int Value { get; set; }
}

public sealed class GraphParent
{
    public GraphChild Child { get; set; } = new();
}

[SharpLink.Sdk.RpcCodecImplementation("graph-child-wire/v1", "graph-child-schema/v1")]
public sealed class GraphChildCodec : SharpLink.Abstractions.IRpcCodec<GraphChild>
{
}

[SharpLink.Sdk.RpcContract]
public interface IGraphContract : SharpLink.Sdk.IService
{
    ValueTask<GraphParent> Echo(GraphParent value, CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(GraphChild), typeof(GraphChildCodec))]");

        var manifest = RunGeneratorAndGetSources(source)
            .Single(static text => text.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));

        Ensure(!manifest.Contains("D:global::GraphChild:", StringComparison.Ordinal),
            "a Contract-owned custom child must not remain in the global Codec graph");
        Ensure(!manifest.Contains("D:global::GraphParent:", StringComparison.Ordinal),
            "a global parent that depends on a Contract-owned child must be removed with that child so the published graph remains closed");
        Ensure(manifest.Contains("K:global::GraphChild:", StringComparison.Ordinal),
            "the selected custom child must be published by the Contract-owned graph");
        Ensure(manifest.Contains("K:global::GraphParent:", StringComparison.Ordinal),
            "the Contract-owned graph must retain the transitive parent that depends on the selected child");
        return Task.CompletedTask;
    }

    [Test]
    public Task UnrelatedContractShouldNotSuppressStandaloneBuiltinOverrideDiagnostic()
    {
        var source = AddAssemblyAttributes(BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class StandaloneBuiltinEnvelope
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IUnrelatedContract : SharpLink.Sdk.IService
{
    ValueTask<string> Echo(string value, CancellationToken cancellationToken);
}

public sealed class StandaloneIntAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "standalone-int/v1";
    public string WireFormatId => "standalone-int-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(StandaloneIntAdapter), \"standalone-int/v1\", \"standalone-int-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int), typeof(StandaloneIntAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK049") == 1,
            "owning an unrelated RPC Contract must not suppress a standalone builtin override diagnostic");
        return Task.CompletedTask;
    }

    [Test]
    public Task ManifestlessReferencedContractShouldNotPublishConsumerDtoDiagnostics()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSource(string.Empty));
        var foreign = CreateMetadataReference(
            "ForeignUnsupportedContract",
            """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace ForeignUnsupportedContract
{
    public interface IForeignPayload
    {
        int Value { get; }
    }

    [RpcContract]
    public interface IForeignContract : IService
    {
        ValueTask<IForeignPayload> Echo(IForeignPayload value, CancellationToken cancellationToken);
    }
}
""",
            sdk);
        const string source = """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

[RpcContract]
public interface ILocalValidContract : IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
""";

        var diagnostics = RunGenerator(source, sdk, foreign);
        Ensure(diagnostics.Length == 0,
            "a manifest-less referenced Contract may participate in static conflict analysis but must not publish DTO diagnostics on the consumer-owned surface");
        return Task.CompletedTask;
    }

    [Test]
    public Task CustomEnumUnderlyingChangeShouldNotChangeSelectedCodecWireIdentity()
    {
        static string ContractSource(string underlyingType) => AddAssemblyAttribute(BuildSource($$"""
public enum StableCustomEnum : {{underlyingType}}
{
    Zero,
    One
}

[SharpLink.Sdk.RpcCodecImplementation("stable-enum-wire/v1", "stable-enum-schema/v1")]
public sealed class StableCustomEnumCodec : SharpLink.Abstractions.IRpcCodec<StableCustomEnum>
{
}

[SharpLink.Sdk.RpcContract]
public interface IStableEnumContract : SharpLink.Sdk.IService
{
    ValueTask<StableCustomEnum> Echo(StableCustomEnum value, CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(StableCustomEnum), typeof(StableCustomEnumCodec))]");

        var baseline = RunContractGenerator(ContractSource("int")).Json;
        var changed = RunContractGenerator(ContractSource("long"), baseline);

        Ensure(changed.Diagnostics.All(static diagnostic => diagnostic.Id != "SHARPLINK032"),
            "once a custom Codec is the final binding, changing the CLR enum underlying type must not manufacture a Native wire-type compatibility change");
        Ensure(!baseline.Contains("\"underlyingType\"", StringComparison.Ordinal),
            "a custom-codec-owned enum must not publish Native enum-underlying metadata into the Contract manifest");
        return Task.CompletedTask;
    }
}
