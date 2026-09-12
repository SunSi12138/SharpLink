using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task OwnerLocalCustomCodecShouldNotDependOnPayloadOwnersUnrelatedManifest()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", UseCurrentIdentitySdk(BuildSource(string.Empty)));
        var payloads = CreateMetadataReference(
            "SharedPayloads",
            """
using System;

[assembly: SharpLink.Abstractions.SharpLinkGeneratedAssemblyManifestAttribute(typeof(SharedPayloads.UnrelatedManifest))]

namespace SharpLink.Abstractions
{
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class SharpLinkGeneratedAssemblyManifestAttribute : Attribute
    {
        public SharpLinkGeneratedAssemblyManifestAttribute(Type manifestType) { }
    }
}

namespace SharedPayloads
{
    public sealed class UnrelatedManifest { }

    public sealed class SharedPayload
    {
        public int Value { get; set; }
    }

    public sealed class SdkReferenceMarker
    {
        public SharpLink.Sdk.IService? Service { get; set; }
    }
}
""",
            sdk);
        const string source = """
using System.Threading;
using System.Threading.Tasks;
using SharedPayloads;
using SharpLink.Abstractions;
using SharpLink.Sdk;

[assembly: RpcCodec(typeof(SharedPayload), typeof(LocalSharedPayloadCodec))]

[RpcCodecSemanticIdentity(0x9001UL, 0xa001UL)]
public sealed class LocalSharedPayloadCodec : IRpcCodec<SharedPayload>
{
}

[RpcContract]
public interface IOwnerLocalContract : IService
{
    ValueTask<SharedPayload> Echo(SharedPayload value, CancellationToken cancellationToken);
}
""";

        var manifest = RunGeneratorAndGetSources(source, sdk, payloads)
            .Single(static text => text.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
        var contractDependenciesStart = manifest.IndexOf("__contractDependencies =", StringComparison.Ordinal);
        var readOnlyStart = manifest.IndexOf("__readOnlyContracts", contractDependenciesStart, StringComparison.Ordinal);
        Ensure(contractDependenciesStart >= 0 && readOnlyStart > contractDependenciesStart,
            "the generated manifest must contain a bounded Contract dependency table");
        var contractDependencies = manifest.Substring(
            contractDependenciesStart,
            readOnlyStart - contractDependenciesStart);
        Ensure(!contractDependencies.Contains("SharedPayloads, Version=", StringComparison.Ordinal),
            "an owner-local custom Codec factory must not depend on an unrelated generated manifest merely because that assembly owns the CLR payload type");
        Ensure(string.Join("\n", RunGeneratorAndGetSources(source, sdk, payloads)).Contains(
                "new global::LocalSharedPayloadCodec()",
                StringComparison.Ordinal),
            "the current Contract owner must construct its local custom Codec directly");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitFrameworkPrimitiveAdapterShouldBeRejectedWithoutRoute()
    {
        var source = AddAssemblyAttributes(UseCurrentIdentitySdk(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface INoRouteBuiltinAdapterContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x9002UL, 0xa002UL)]
public sealed class ExplicitIntAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "explicit.no-route-int/v1";
    public string WireFormatId => "explicit-no-route-int-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
""")),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ExplicitIntAdapter), \"explicit.no-route-int/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int), typeof(ExplicitIntAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK049"),
            "framework primitive int must reject explicit Adapter rebinding without depending on route configuration");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("explicit-no-route-int-wire/v1\";", StringComparison.Ordinal),
            "a rejected framework primitive Adapter must not enter the final Contract graph");
        return Task.CompletedTask;
    }

    [Test]
    public Task ReferencedManifestlessContractPolicyShouldNotBecomeConsumerOwned()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", UseCurrentIdentitySdk(BuildSource(string.Empty)));
        var foreign = CreateMetadataReference(
            "ForeignContracts",
            """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace ForeignContracts
{
    public sealed class ForeignPayload
    {
        public int Value { get; set; }
    }

    [RpcContract]
    public interface IForeignContract : IService
    {
        ValueTask<ForeignPayload> Echo(ForeignPayload value, CancellationToken cancellationToken);
    }
}
""",
            sdk);
        const string source = """
using System.Threading;
using System.Threading.Tasks;
using ForeignContracts;
using SharpLink.Abstractions;
using SharpLink.Sdk;

[assembly: RpcCodec(typeof(ForeignPayload), typeof(ForeignPayloadCodec))]

[RpcCodecSemanticIdentity(0x9003UL, 0xa003UL)]
public sealed class ForeignPayloadCodec : IRpcCodec<ForeignPayload>
{
}

[RpcContract]
public interface ILocalContract : IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
""";

        var generated = RunGeneratorAndGetSources(source, sdk, foreign);
        var allGenerated = string.Join("\n", generated);
        Ensure(!allGenerated.Contains("new global::ForeignPayloadCodec()", StringComparison.Ordinal),
            "a manifest-less referenced Contract must not cause its payload policy to be published as consumer-owned ContractCodecs");
        Ensure(!allGenerated.Contains("foreign-consumer-wire/v1", StringComparison.Ordinal),
            "foreign Contract payload identity must remain outside the current owner's generated Codec graph");
        return Task.CompletedTask;
    }

    [Test]
    public Task FrameworkEnumCustomCodecShouldBeRejectedForDirectAndNestedUse()
    {
        var source = AddAssemblyAttribute(UseCurrentIdentitySdk(BuildSource("""
public enum CustomMode : short
{
    Zero,
    One
}

public sealed class CustomEnvelope
{
    public CustomMode Mode { get; set; }
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x9004UL, 0xa004UL)]
public sealed class CustomModeCodec : SharpLink.Abstractions.IRpcCodec<CustomMode>
{
}

[SharpLink.Sdk.RpcContract]
public interface ICustomModeContract : SharpLink.Sdk.IService
{
    ValueTask<CustomMode> EchoMode(CustomMode value, CancellationToken cancellationToken);
    ValueTask<CustomEnvelope> EchoEnvelope(CustomEnvelope value, CancellationToken cancellationToken);
}
""")),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(CustomMode), typeof(CustomModeCodec))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK063"),
            "framework enum wire semantics must reject custom Codec rebinding regardless of graph position");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("new global::CustomModeCodec()", StringComparison.Ordinal),
            "a rejected enum custom Codec must not be published");
        return Task.CompletedTask;
    }

    [Test]
    public Task FrameworkStringCustomCodecShouldBeRejectedForDirectAndNestedUse()
    {
        var source = AddAssemblyAttribute(UseCurrentIdentitySdk(BuildSource("""
public sealed class StringEnvelope
{
    public string Value { get; set; } = string.Empty;
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x9005UL, 0xa005UL)]
public sealed class OwnerStringCodec : SharpLink.Abstractions.IRpcCodec<string>
{
}

[SharpLink.Sdk.RpcContract]
public interface IStringOwnerContract : SharpLink.Sdk.IService
{
    ValueTask<string> EchoString(string value, CancellationToken cancellationToken);
    ValueTask<StringEnvelope> EchoEnvelope(StringEnvelope value, CancellationToken cancellationToken);
}
""")),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(string), typeof(OwnerStringCodec))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK063"),
            "framework string wire semantics must reject custom Codec rebinding regardless of graph position");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("new global::OwnerStringCodec()", StringComparison.Ordinal),
            "a rejected string custom Codec must not be published");
        return Task.CompletedTask;
    }
}
