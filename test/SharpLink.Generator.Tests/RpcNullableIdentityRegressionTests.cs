using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task OptionalReferenceNullableAnnotationShouldNotChangeFinalIdentity()
    {
        var nullable = GenerateNullableMemberIdentityManifest(required: false, nullable: true);
        var nonNullable = GenerateNullableMemberIdentityManifest(required: false, nullable: false);

        Ensure(
            ExtractGeneratedCodecIdentity(nullable, "NullableMemberPayload") ==
            ExtractGeneratedCodecIdentity(nonNullable, "NullableMemberPayload"),
            "optional reference nullable annotations must not perturb DTO CodecHash when generated null behavior is unchanged");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(nullable) == ExtractGeneratedRpcAssemblyHash(nonNullable),
            "optional reference nullable annotations must not perturb RpcAssemblyHash when RPC semantics are unchanged");
        return Task.CompletedTask;
    }

    [Test]
    public Task RequiredReferenceNullRejectionShouldChangeFinalIdentity()
    {
        var nullable = GenerateNullableMemberIdentityManifest(required: true, nullable: true);
        var nonNullable = GenerateNullableMemberIdentityManifest(required: true, nullable: false);

        Ensure(
            ExtractGeneratedCodecIdentity(nullable, "NullableMemberPayload") !=
            ExtractGeneratedCodecIdentity(nonNullable, "NullableMemberPayload"),
            "required non-null reference rejection is an effective decode semantic and must change DTO CodecHash");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(nullable) != ExtractGeneratedRpcAssemblyHash(nonNullable),
            "required non-null reference rejection must propagate into RpcAssemblyHash");
        return Task.CompletedTask;
    }

    private static string GenerateNullableMemberIdentityManifest(bool required, bool nullable)
    {
        var requiredAttribute = required ? "[SharpLink.Sdk.RpcRequired]" : string.Empty;
        var memberType = nullable ? "string?" : "string";
        var source = BuildSource($$"""
#nullable enable
[SharpLink.Sdk.RpcSerializable]
public sealed class NullableMemberPayload
{
    {{requiredAttribute}}
    public {{memberType}} Name { get; set; } = null!;
}

[SharpLink.Sdk.RpcContract]
public interface INullableMemberIdentityContract : SharpLink.Sdk.IService
{
    ValueTask<NullableMemberPayload> Echo(
        NullableMemberPayload value,
        CancellationToken cancellationToken);
}
""");

        return RunGeneratorAndGetSources(source)
            .Single(static generated =>
                generated.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));
    }
}
