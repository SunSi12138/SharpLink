using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task EnumValueMappingShouldParticipateInDirectAndDtoCodecIdentity()
    {
        static string DirectSource(bool swapped)
        {
            var members = swapped ? "Ok = 1, Error = 0" : "Ok = 0, Error = 1";
            return BuildSource($$"""
public enum Status : byte { {{members}} }

[SharpLink.Sdk.RpcContract]
public interface IDirectEnumIdentityContract : SharpLink.Sdk.IService
{
    ValueTask<Status> Echo(Status value, CancellationToken cancellationToken);
}
""");
        }

        static string DtoSource(bool swapped)
        {
            var members = swapped ? "Ok = 1, Error = 0" : "Ok = 0, Error = 1";
            return BuildSource($$"""
public enum Status : byte { {{members}} }

[SharpLink.Sdk.RpcSerializable]
public sealed class EnumEnvelope
{
    public Status Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IDtoEnumIdentityContract : SharpLink.Sdk.IService
{
    ValueTask<EnumEnvelope> Echo(EnumEnvelope value, CancellationToken cancellationToken);
}
""");
        }

        static string Manifest(string source)
            => RunGeneratorAndGetSources(source)
                .Single(static generated => generated.Contains("ISharpLinkGeneratedAssemblyManifest", StringComparison.Ordinal));

        var directBaseline = Manifest(DirectSource(swapped: false));
        var directChanged = Manifest(DirectSource(swapped: true));
        Ensure(
            ExtractGeneratedRpcAssemblyHash(directBaseline) != ExtractGeneratedRpcAssemblyHash(directChanged),
            "swapping enum name/value mappings must change RpcAssemblyHash for a direct enum contract even when the underlying byte width is unchanged");

        var dtoBaseline = Manifest(DtoSource(swapped: false));
        var dtoChanged = Manifest(DtoSource(swapped: true));
        Ensure(
            ExtractGeneratedCodecIdentity(dtoBaseline, "EnumEnvelope") != ExtractGeneratedCodecIdentity(dtoChanged, "EnumEnvelope"),
            "enum declaration identity must propagate through fixed DTO members");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(dtoBaseline) != ExtractGeneratedRpcAssemblyHash(dtoChanged),
            "the DTO enum mapping change must propagate into RpcAssemblyHash");
        return Task.CompletedTask;
    }
}
