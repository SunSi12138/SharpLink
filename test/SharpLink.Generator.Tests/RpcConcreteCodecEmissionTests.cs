using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task CompileTimeKnownNativeCodecsShouldRemainConcreteAcrossGeneratedArtifacts()
    {
        var source = BuildSource("""
public sealed class ChildPayload
{
    public int Value { get; set; }
}

public sealed class EnvelopePayload
{
    public ChildPayload Child { get; set; } = new();
    public List<ChildPayload> Items { get; set; } = new();
}

[SharpLink.Sdk.RpcUnionCase(1, typeof(ChoicePayload))]
public interface IChoicePayload
{
}

public sealed class ChoicePayload : IChoicePayload
{
    public ChildPayload Child { get; set; } = new();
}

[SharpLink.Sdk.RpcContract]
public interface IConcreteCodecContract : SharpLink.Sdk.IService
{
    ValueTask<EnvelopePayload> Echo(
        EnvelopePayload value,
        IAsyncEnumerable<ChildPayload> items,
        CancellationToken cancellationToken);

    ValueTask<IChoicePayload> EchoChoice(
        IChoicePayload value,
        CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        var lines = generated.Split('\n');

        Ensure(lines.Any(static line =>
                line.Contains("private readonly global::SharpLink.Generated.__SharpLinkGeneratedCodec_", StringComparison.Ordinal) &&
                line.Contains("__parameterCodec_", StringComparison.Ordinal)),
            "Stub parameter Codec fields with local generated implementations must use the concrete generated Codec type");
        Ensure(lines.Any(static line =>
                line.Contains("private readonly global::SharpLink.Generated.__SharpLinkGeneratedCodec_", StringComparison.Ordinal) &&
                line.Contains("__responseCodec_", StringComparison.Ordinal)),
            "Stub/Proxy response Codec fields with local generated implementations must use the concrete generated Codec type");
        Ensure(lines.Any(static line =>
                line.Contains("private readonly global::SharpLink.Generated.__SharpLinkGeneratedCodec_", StringComparison.Ordinal) &&
                line.Contains("__streamCodec_", StringComparison.Ordinal)),
            "Proxy stream-item Codec fields with local generated implementations must use the concrete generated Codec type");
        Ensure(lines.Any(static line =>
                line.Contains("private readonly global::SharpLink.Generated.__SharpLinkGeneratedCodec_", StringComparison.Ordinal) &&
                line.Contains("__codec_value", StringComparison.Ordinal)),
            "generated request Codec dependencies must retain the concrete generated Codec type");
        Ensure(lines.Any(static line =>
                line.Contains("private readonly global::SharpLink.Generated.__SharpLinkGeneratedCodec_", StringComparison.Ordinal) &&
                line.Contains("__codec_0", StringComparison.Ordinal)),
            "generated DTO/Union/Collection child Codec fields must retain concrete generated Codec types");
        Ensure(lines.Any(static line =>
                line.Contains("__responseCodec_", StringComparison.Ordinal) &&
                line.Contains(".Serialize(result, output);", StringComparison.Ordinal)),
            "non-streaming Stub response encode must call Serialize through the concrete response field");
        Ensure(!generated.Contains("__SerializeResponse(", StringComparison.Ordinal),
            "Stub response encode must not re-erase a concrete Codec through the old IRpcCodec<T> helper");
        return Task.CompletedTask;
    }

    [Test]
    public Task CustomCodecShouldBeConcreteWhileAdapterCodecKeepsInterfaceFallback()
    {
        var source = AddAssemblyAttributes(UseCurrentIdentitySdk(BuildSource("""
public sealed class CustomPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x7261UL, 0x7262UL)]
public sealed class CustomPayloadCodec : SharpLink.Abstractions.IRpcCodec<CustomPayload>
{
}

public sealed class AdaptedPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x7263UL, 0x7264UL)]
public sealed class AdaptedPayloadAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "issue726-adapter/v1";
    public string WireFormatId => "issue726-adapter-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

[SharpLink.Sdk.RpcContract]
public interface IConcreteBindingContract : SharpLink.Sdk.IService
{
    ValueTask<CustomPayload> EchoCustom(CustomPayload value, CancellationToken cancellationToken);
    ValueTask<AdaptedPayload> EchoAdapted(AdaptedPayload value, CancellationToken cancellationToken);
}
""")),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(CustomPayload), typeof(CustomPayloadCodec))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AdaptedPayloadAdapter), \"issue726-adapter/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(AdaptedPayload), typeof(AdaptedPayloadAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        var lines = generated.Split('\n');

        Ensure(lines.Any(static line =>
                line.Contains("private readonly global::CustomPayloadCodec ", StringComparison.Ordinal)),
            "closed public sealed custom Codecs with frozen Contract bindings must use their concrete type");
        Ensure(generated.Contains(
                "(global::CustomPayloadCodec)codecs.GetCodec<global::CustomPayload>()",
                StringComparison.Ordinal) ||
               generated.Contains(
                "(global::CustomPayloadCodec)__codecs.GetCodec<global::CustomPayload>()",
                StringComparison.Ordinal),
            "custom Codec resolution must cast once at generated binding construction");
        Ensure(lines.Any(static line =>
                line.Contains("private readonly IRpcCodec<global::AdaptedPayload>", StringComparison.Ordinal)),
            "Adapter-backed Codecs must keep IRpcCodec<T> storage because the concrete runtime implementation is not statically known");
        Ensure(!generated.Contains(
                "(global::AdaptedPayloadAdapter)codecs.GetCodec<global::AdaptedPayload>()",
                StringComparison.Ordinal),
            "Adapter types are factories, not concrete IRpcCodec<T> implementations, and must never be used as field types");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitInterfaceCustomCodecShouldKeepInterfaceFallback()
    {
        var source = AddAssemblyAttribute(UseCurrentIdentitySdk(BuildSource("""
public sealed class ExplicitPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x7267UL, 0x7268UL)]
public sealed class ExplicitPayloadCodec : SharpLink.Abstractions.IRpcCodec<ExplicitPayload>
{
    void SharpLink.Abstractions.IRpcCodec<ExplicitPayload>.Serialize(
        in ExplicitPayload value,
        System.Buffers.IBufferWriter<byte> buffer) => throw new NotImplementedException();

    ExplicitPayload? SharpLink.Abstractions.IRpcCodec<ExplicitPayload>.Deserialize(
        in System.Buffers.ReadOnlySequence<byte> buffer) => throw new NotImplementedException();
}

[SharpLink.Sdk.RpcContract]
public interface IExplicitCodecContract : SharpLink.Sdk.IService
{
    ValueTask<ExplicitPayload> Echo(ExplicitPayload value, CancellationToken cancellationToken);
}
""")),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(ExplicitPayload), typeof(ExplicitPayloadCodec))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains(
                "private readonly IRpcCodec<global::ExplicitPayload>",
                StringComparison.Ordinal),
            "a custom Codec with explicit interface implementations must retain interface storage because direct concrete calls are not callable");
        Ensure(!generated.Contains(
                "private readonly global::ExplicitPayloadCodec ",
                StringComparison.Ordinal),
            "explicit-interface custom Codec implementations must not be emitted as direct concrete fields");
        return Task.CompletedTask;
    }

    [Test]
    public Task ReferencedGeneratedCodecShouldKeepInterfaceFallback()
    {
        var support = CreateMetadataReference(
            "Issue726ReferencedSupport",
            """
using System;

namespace SharpLink.Abstractions
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class SharpLinkGeneratedCodecIdentityAttribute : Attribute
    {
        public SharpLinkGeneratedCodecIdentityAttribute(Type targetType, ulong high, ulong low) { }
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class SharpLinkGeneratedAssemblyManifestAttribute : Attribute
    {
        public SharpLinkGeneratedAssemblyManifestAttribute(
            Type manifestType,
            int apiVersion,
            int protocolVersion,
            string generatorVersion,
            string abiIdentity) { }
    }
}
""");
        var owner = CreateMetadataReference(
            "Issue726ReferencedOwner",
            """
using SharpLink.Abstractions;

[assembly: SharpLinkGeneratedCodecIdentityAttribute(typeof(Vendor.ReferencedPayload), 0x7265UL, 0x7266UL)]
[assembly: SharpLinkGeneratedAssemblyManifestAttribute(
    typeof(Vendor.Manifest),
    4,
    2,
    "2.0.0",
    "sharplink-2.0-api4-rpcchannel-codec-provider-v1")]

namespace Vendor
{
    public sealed class ReferencedPayload { }
    public sealed class Manifest { }
}
""",
            support);
        var source = BuildSource("""
public sealed class ReferencedEnvelope
{
    public Vendor.ReferencedPayload Value { get; set; } = new();
}

[SharpLink.Sdk.RpcContract]
public interface IReferencedCodecContract : SharpLink.Sdk.IService
{
    ValueTask<ReferencedEnvelope> Echo(ReferencedEnvelope value, CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source, support, owner));
        Ensure(generated.Contains(
                "private readonly IRpcCodec<global::Vendor.ReferencedPayload> __codec_0;",
                StringComparison.Ordinal),
            "referenced generated Codec implementations are internal to the provider assembly and must keep interface fallback");
        return Task.CompletedTask;
    }
}
