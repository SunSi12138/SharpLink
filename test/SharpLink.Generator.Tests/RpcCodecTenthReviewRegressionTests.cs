using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ClosedGenericCustomCodecShouldUseSelectedSymbolAndClosedTargetIdentity()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcCodec(typeof(GenericCodec<FirstPayload>))]
public sealed class FirstPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcCodec(typeof(GenericCodec<SecondPayload>))]
public sealed class SecondPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x1111111111111111UL, 0x2222222222222222UL)]
public sealed class GenericCodec<T> : SharpLink.Abstractions.IRpcCodec<T>
{
}

[SharpLink.Sdk.RpcContract]
public interface IClosedGenericCustomContract : SharpLink.Sdk.IService
{
    ValueTask<FirstPayload> EchoFirst(FirstPayload value, CancellationToken cancellationToken);
    ValueTask<SecondPayload> EchoSecond(SecondPayload value, CancellationToken cancellationToken);
}
""");

        var manifest = RunGeneratorAndGetSources(source)
            .Single(static generated => generated.Contains(
                "ISharpLinkGeneratedAssemblyManifest",
                StringComparison.Ordinal));

        Ensure(
            ExtractGeneratedCodecIdentity(manifest, "FirstPayload") !=
            ExtractGeneratedCodecIdentity(manifest, "SecondPayload"),
            "closed generic custom Codec targets must not collapse to the generic definition's shared opaque identity");
        return Task.CompletedTask;
    }

    [Test]
    public Task ClosedGenericAdapterShouldUseSelectedImplementationSymbol()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(GenericAdapter<AdapterPayload>))]
public sealed class AdapterPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x3333333333333333UL, 0x4444444444444444UL)]
public sealed class GenericAdapter<T> : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "generic-adapter/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

[SharpLink.Sdk.RpcContract]
public interface IClosedGenericAdapterContract : SharpLink.Sdk.IService
{
    ValueTask<AdapterPayload> Echo(AdapterPayload value, CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(GenericAdapter<AdapterPayload>), \"generic-adapter/v1\")]");

        var changedIdentitySource = source.Replace(
            "0x3333333333333333UL",
            "0x7333333333333333UL",
            StringComparison.Ordinal);
        var manifest = RunGeneratorAndGetSources(source)
            .Single(static generated => generated.Contains(
                "ISharpLinkGeneratedAssemblyManifest",
                StringComparison.Ordinal));
        var changedManifest = RunGeneratorAndGetSources(changedIdentitySource)
            .Single(static generated => generated.Contains(
                "ISharpLinkGeneratedAssemblyManifest",
                StringComparison.Ordinal));
        Ensure(
            ExtractGeneratedRpcAssemblyHash(manifest) != ExtractGeneratedRpcAssemblyHash(changedManifest),
            "a valid constructed generic Adapter must retain the semantic identity of its selected implementation symbol");
        return Task.CompletedTask;
    }

    [Test]
    public Task SameFqnCustomCodecShouldUseAliasedSelectedSymbolIdentity()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSource(string.Empty));
        var payload = CreateMetadataReference(
            "SameFqnPayload",
            "namespace Shared { public sealed class Payload { public int Value { get; set; } } }");

        static MetadataReference Alias(MetadataReference reference, string alias)
            => ((PortableExecutableReference)reference).WithAliases(ImmutableArray.Create(alias));

        var codecA = Alias(CreateMetadataReference(
            "SameFqnCodecA",
            """
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SameName
{
    [RpcCodecSemanticIdentity(0xaaaaaaaaaaaaaaaaUL, 0x1111111111111111UL)]
    public sealed class PayloadCodec : IRpcCodec<Shared.Payload> { }
}
""",
            sdk,
            payload), "CodecA");
        var codecB = Alias(CreateMetadataReference(
            "SameFqnCodecB",
            """
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SameName
{
    [RpcCodecSemanticIdentity(0xbbbbbbbbbbbbbbbbUL, 0x2222222222222222UL)]
    public sealed class PayloadCodec : IRpcCodec<Shared.Payload> { }
}
""",
            sdk,
            payload), "CodecB");

        static string Consumer(string alias) => $$"""
extern alias CodecA;
extern alias CodecB;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

[assembly: RpcCodec(typeof(Shared.Payload), typeof({{alias}}::SameName.PayloadCodec))]

[RpcContract]
public interface ISameFqnCodecContract : IService
{
    ValueTask<Shared.Payload> Echo(Shared.Payload value, CancellationToken cancellationToken);
}
""";

        var manifestA = RunGeneratorAndGetSources(Consumer("CodecA"), sdk, payload, codecA, codecB)
            .Single(static generated => generated.Contains(
                "ISharpLinkGeneratedAssemblyManifest",
                StringComparison.Ordinal));
        var manifestB = RunGeneratorAndGetSources(Consumer("CodecB"), sdk, payload, codecA, codecB)
            .Single(static generated => generated.Contains(
                "ISharpLinkGeneratedAssemblyManifest",
                StringComparison.Ordinal));

        Ensure(
            ExtractGeneratedRpcAssemblyHash(manifestA) != ExtractGeneratedRpcAssemblyHash(manifestB),
            "same-FQN implementations from different referenced assemblies must use the semantic identity of the actually selected symbol");
        return Task.CompletedTask;
    }

    [Test]
    public Task ReferencedCodecHashShouldRequireCurrentGeneratedAbi()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSource(string.Empty));

        static MetadataReference GeneratedPayloadReference(string assemblyName, string abiIdentity)
            => CreateMetadataReference(
                assemblyName,
                $$"""
using System;

[assembly: SharpLink.Abstractions.SharpLinkGeneratedCodecIdentityAttribute(typeof(Referenced.Payload), 0x5555555555555555UL, 0x6666666666666666UL)]
[assembly: SharpLink.Abstractions.SharpLinkGeneratedAssemblyManifestAttribute(typeof(Referenced.Manifest), 4, 2, "2.0.0-test", "{{abiIdentity}}")] 

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

namespace Referenced
{
    public sealed class Payload { public int Value { get; set; } }
    public sealed class Manifest { }
}
""");

        const string consumer = """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

[RpcContract]
public interface IReferencedCodecContract : IService
{
    ValueTask<Referenced.Payload> Echo(Referenced.Payload value, CancellationToken cancellationToken);
}
""";

        var stale = GeneratedPayloadReference(
            "StaleGeneratedPayload",
            "sharplink-2.0-api4-rpcchannel-codec-provider-v3");
        var staleDiagnostics = RunGenerator(consumer, sdk, stale);
        Ensure(
            staleDiagnostics.Any(static diagnostic =>
                diagnostic.GetMessage().Contains("incompatible SharpLink generated ABI", StringComparison.Ordinal) &&
                diagnostic.GetMessage().Contains("Rebuild/regenerate", StringComparison.Ordinal)),
            $"a referenced CodecHash from an old generated ABI must be rejected with a rebuild/regenerate diagnostic. Actual: {FormatDiagnostics(staleDiagnostics)}");

        var current = GeneratedPayloadReference(
            "CurrentGeneratedPayload",
            "sharplink-2.0-api4-rpcchannel-codec-provider-v4");
        var currentDiagnostics = RunGenerator(consumer, sdk, current);
        Ensure(
            !currentDiagnostics.Any(static diagnostic =>
                diagnostic.GetMessage().Contains("incompatible SharpLink generated ABI", StringComparison.Ordinal)),
            "a referenced CodecHash produced by the current generated ABI must remain accepted");
        return Task.CompletedTask;
    }
}
