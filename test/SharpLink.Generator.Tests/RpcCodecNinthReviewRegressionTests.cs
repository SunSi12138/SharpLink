using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task AdapterClosedGenericTargetShouldIncludeEveryNamedTypeAssemblyIdentity()
    {
        static MetadataReference CreateSharedDtoReference(string assemblyName)
            => ((PortableExecutableReference)CreateMetadataReference(
                    assemblyName,
                    "namespace Shared { public sealed class Dto { } }"))
                .WithAliases(ImmutableArray.Create("SharedRef"));

        var source = "extern alias SharedRef;\n" + AddAssemblyAttribute(BuildSource("""
[FakePackable]
public sealed class Wrapper<T>
{
}

[SharpLink.Sdk.RpcContract]
public interface IClosedAdapterContract : SharpLink.Sdk.IService
{
    ValueTask<Wrapper<SharedRef::Shared.Dto>> Echo(
        Wrapper<SharedRef::Shared.Dto> value,
        CancellationToken cancellationToken);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class FakePackableAttribute : Attribute { }

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x3131313131313131UL, 0x4242424242424242UL)]
public sealed class StableAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "stable-generic-adapter/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(StableAdapter), \"stable-generic-adapter/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");

        static string Manifest(string source, MetadataReference reference)
            => RunGeneratorAndGetSources(source, reference)
                .Single(static generated => generated.Contains(
                    "ISharpLinkGeneratedAssemblyManifest",
                    StringComparison.Ordinal));

        var assemblyA = Manifest(source, CreateSharedDtoReference("SharedDto.A"));
        var assemblyB = Manifest(source, CreateSharedDtoReference("SharedDto.B"));

        Ensure(
            ExtractGeneratedRpcAssemblyHash(assemblyA) != ExtractGeneratedRpcAssemblyHash(assemblyB),
            "closed Adapter identity must distinguish same-named generic arguments from different logical assemblies");
        return Task.CompletedTask;
    }

    [Test]
    public Task NullableUnmanagedFallbackIdentityShouldMirrorRuntimeUnsafeBlitSelection()
    {
        static string Manifest(string source)
            => RunGeneratorAndGetSources(source)
                .Single(static generated => generated.Contains(
                    "ISharpLinkGeneratedAssemblyManifest",
                    StringComparison.Ordinal));

        static string EnumSource(bool swapped)
        {
            var members = swapped ? "Ok = 1, Error = 0" : "Ok = 0, Error = 1";
            return BuildSource($$"""
public enum NullableStatus : int { {{members}} }

[SharpLink.Sdk.RpcContract]
public interface INullableEnumContract : SharpLink.Sdk.IService
{
    ValueTask<NullableStatus?> Echo(NullableStatus? value, CancellationToken cancellationToken);
}
""");
        }

        var enumBefore = Manifest(EnumSource(swapped: false));
        var enumAfter = Manifest(EnumSource(swapped: true));
        Ensure(
            ExtractGeneratedRpcAssemblyHash(enumBefore) != ExtractGeneratedRpcAssemblyHash(enumAfter),
            "Nullable<enum> uses runtime UnsafeBlit bytes but must still retain the enum declaration semantic identity");

        static string DtoSource(int fieldId, string physicalType)
            => BuildSource($$"""
[SharpLink.Sdk.RpcSerializable]
public struct NullablePayload
{
    [SharpLink.Sdk.RpcMember({{fieldId}})]
    public {{physicalType}} Value;
}

[SharpLink.Sdk.RpcContract]
public interface INullableDtoContract : SharpLink.Sdk.IService
{
    ValueTask<NullablePayload?> Echo(NullablePayload? value, CancellationToken cancellationToken);
}
""");

        var fieldOne = Manifest(DtoSource(fieldId: 1, physicalType: "int"));
        var fieldSeven = Manifest(DtoSource(fieldId: 7, physicalType: "int"));
        Ensure(
            ExtractGeneratedCodecIdentity(fieldOne, "NullablePayload") !=
            ExtractGeneratedCodecIdentity(fieldSeven, "NullablePayload"),
            "changing RpcMember identity must still change the generated child DTO CodecHash");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(fieldOne) == ExtractGeneratedRpcAssemblyHash(fieldSeven),
            "Nullable<RpcSerializableStruct> must model the runtime raw Nullable<T> layout rather than composing the child DTO CodecHash");

        var intLayout = fieldOne;
        var longLayout = Manifest(DtoSource(fieldId: 1, physicalType: "long"));
        Ensure(
            ExtractGeneratedRpcAssemblyHash(intLayout) != ExtractGeneratedRpcAssemblyHash(longLayout),
            "changing the physical Nullable<T> layout must change the advertised runtime UnsafeBlit identity");
        return Task.CompletedTask;
    }
}
