using System;
using System.Linq;
using System.Threading.Tasks;
using SharpLink.Abstractions;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task AdapterFreeFactoryKindShouldDistinguishNativeAndDirectWireIdentities()
    {
        IRpcGeneratedCodecFactory custom = new AdapterFreeFactory("review-custom/v1");
        IRpcGeneratedCodecFactory native = new AdapterFreeFactory("sharplink-native/v1");

        Ensure(custom.Kind == RpcGeneratedCodecFactoryKind.Direct,
            "adapter-free factories with a non-native wire identity must be treated as direct/custom construction");
        Ensure(native.Kind == RpcGeneratedCodecFactoryKind.Native,
            "the SharpLink native wire identity must retain the Native factory kind");
        return Task.CompletedTask;
    }

    [Test]
    public Task ContractOnlyCustomCodecShouldBeOwnedWithoutChangingStandaloneCustomCodecPublication()
    {
        var contractSource = AddAssemblyAttribute(BuildSource("""
public sealed class ContractOnlyPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcCodecImplementation("contract-only-wire/v1", "contract-only-schema/v1")]
public sealed class ContractOnlyPayloadCodec : SharpLink.Abstractions.IRpcCodec<ContractOnlyPayload>
{
}

[SharpLink.Sdk.RpcContract]
public interface IContractOnlyCodecService : SharpLink.Sdk.IService
{
    ValueTask<ContractOnlyPayload> Echo(ContractOnlyPayload value, CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(ContractOnlyPayload), typeof(ContractOnlyPayloadCodec))]");

        var contractManifest = RunGeneratorAndGetSources(contractSource)
            .Single(static text => text.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        var contractSections = GetCodecManifestSections(contractManifest);
        Ensure(!contractSections.Global.Contains(".Factory(),", StringComparison.Ordinal),
            "a Contract-only explicit custom Codec must not leak into the context-global Codec table");
        Ensure(contractSections.Contract.Contains(".Factory(),", StringComparison.Ordinal),
            "a Contract-only explicit custom Codec must be published in the assembly-owned Contract Codec table");

        var standaloneSource = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
[SharpLink.Sdk.RpcCodec(typeof(StandalonePayloadCodec))]
public sealed class StandalonePayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcCodecImplementation("standalone-wire/v1", "standalone-schema/v1")]
public sealed class StandalonePayloadCodec : SharpLink.Abstractions.IRpcCodec<StandalonePayload>
{
}
""");

        var standaloneManifest = RunGeneratorAndGetSources(standaloneSource)
            .Single(static text => text.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        var standaloneSections = GetCodecManifestSections(standaloneManifest);
        Ensure(standaloneSections.Global.Contains(".Factory(),", StringComparison.Ordinal),
            "standalone [RpcSerializable] custom Codec publication must remain in the normal/global table");
        Ensure(!standaloneSections.Contract.Contains(".Factory(),", StringComparison.Ordinal),
            "standalone-only custom Codec publication must not create Contract policy");
        return Task.CompletedTask;
    }

    [Test]
    public Task GeneratedManifestShouldSeparateContractOnlyCodecDependencies()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSource(string.Empty));
        var payload = CreateMetadataReference(
            "ReviewPayloads",
            "namespace ReviewPayloads { public sealed class Payload { public int Value { get; set; } } }");
        var codec = CreateMetadataReference(
            "ReviewPayloadCodecs",
            """
using ReviewPayloads;
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace ReviewPayloadCodecs
{
    [RpcCodecImplementation("review-payload-wire/v1", "review-payload-schema/v1")]
    public sealed class PayloadCodec : IRpcCodec<Payload>
    {
    }
}
""",
            sdk,
            payload);
        var source = """
using System.Threading;
using System.Threading.Tasks;
using ReviewPayloads;
using ReviewPayloadCodecs;
using SharpLink.Sdk;

[assembly: RpcCodec(typeof(Payload), typeof(PayloadCodec))]

[RpcContract]
public interface IReviewPayloadContract : IService
{
    ValueTask<Payload> Echo(Payload value, CancellationToken cancellationToken);
}
""";

        var manifest = RunGeneratorAndGetSources(source, sdk, payload, codec)
            .Single(static text => text.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        var dependenciesStart = manifest.IndexOf("__dependencies =", StringComparison.Ordinal);
        var contractDependenciesStart = manifest.IndexOf("__contractDependencies =", StringComparison.Ordinal);
        var readOnlyStart = manifest.IndexOf("__readOnlyContracts", contractDependenciesStart, StringComparison.Ordinal);
        Ensure(dependenciesStart >= 0 && contractDependenciesStart > dependenciesStart && readOnlyStart > contractDependenciesStart,
            "generated manifests must publish distinct normal and Contract dependency tables");

        var normalDependencies = manifest.Substring(
            dependenciesStart,
            contractDependenciesStart - dependenciesStart);
        var contractDependencies = manifest.Substring(
            contractDependenciesStart,
            readOnlyStart - contractDependenciesStart);
        Ensure(normalDependencies.Contains("ReviewPayloads", StringComparison.Ordinal),
            "the Contract payload assembly remains a normal manifest dependency");
        Ensure(!normalDependencies.Contains("ReviewPayloadCodecs", StringComparison.Ordinal),
            "the RPC-only Codec implementation assembly must not leak into normal Dependencies");
        Ensure(contractDependencies.Contains("ReviewPayloadCodecs", StringComparison.Ordinal),
            "the RPC-only Codec implementation assembly must be published through ContractDependencies");
        Ensure(!contractDependencies.Contains("ReviewPayloads", StringComparison.Ordinal),
            "ContractDependencies should contain only the RPC-only dependency delta");
        return Task.CompletedTask;
    }

    private static (string Global, string Contract) GetCodecManifestSections(string manifest)
    {
        var globalStart = manifest.IndexOf("__codecs =", StringComparison.Ordinal);
        var contractStart = manifest.IndexOf("__contractCodecs =", StringComparison.Ordinal);
        var dependenciesStart = manifest.IndexOf("__dependencies =", contractStart, StringComparison.Ordinal);
        Ensure(globalStart >= 0 && contractStart > globalStart && dependenciesStart > contractStart,
            "generated manifest must expose ordered global and Contract Codec tables");
        return (
            manifest.Substring(globalStart, contractStart - globalStart),
            manifest.Substring(contractStart, dependenciesStart - contractStart));
    }

    private sealed class AdapterFreeFactory(string wireFormatId) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(AdapterFreeFactory);
        public string SchemaId => "review-factory-schema/v1";
        public string WireFormatId { get; } = wireFormatId;
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => throw new NotSupportedException();

        public bool IsCompatibleCodec(IRpcCodec codec) => false;
    }
}
