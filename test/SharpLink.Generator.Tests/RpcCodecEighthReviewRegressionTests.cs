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

        static string EnumCodecHash(string contractManifestJson)
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(contractManifestJson)!.AsObject();
            var enumEntry = root["enums"]!.AsArray()
                .Select(static item => item!.AsObject())
                .Single(static item => item["name"]!.GetValue<string>() == "Status");
            var codecHash = enumEntry["codecHash"]?.GetValue<string>();
            Ensure(IsValidCodecHashText(codecHash), "enum manifest entry must persist a fixed-width CodecHash");
            return codecHash!;
        }

        var directBaselineSource = DirectSource(swapped: false);
        var directChangedSource = DirectSource(swapped: true);
        var directBaseline = Manifest(directBaselineSource);
        var directChanged = Manifest(directChangedSource);
        Ensure(
            ExtractGeneratedRpcAssemblyHash(directBaseline) != ExtractGeneratedRpcAssemblyHash(directChanged),
            "swapping enum name/value mappings must change RpcAssemblyHash for a direct enum contract even when the underlying byte width is unchanged");

        var directBaselineManifest = RunContractGenerator(directBaselineSource).Json;
        var directChangedManifest = RunContractGenerator(directChangedSource, directBaselineManifest);
        Ensure(
            EnumCodecHash(directBaselineManifest) != EnumCodecHash(directChangedManifest.Json),
            "the v3 contract manifest must persist the same enum semantic CodecHash used by runtime identity");
        Ensure(
            directChangedManifest.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "swapping direct enum name/value mappings must fail contract baseline comparison");

        var dtoBaselineSource = DtoSource(swapped: false);
        var dtoChangedSource = DtoSource(swapped: true);
        var dtoBaseline = Manifest(dtoBaselineSource);
        var dtoChanged = Manifest(dtoChangedSource);
        Ensure(
            ExtractGeneratedCodecIdentity(dtoBaseline, "EnumEnvelope") != ExtractGeneratedCodecIdentity(dtoChanged, "EnumEnvelope"),
            "enum declaration identity must propagate through fixed DTO members");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(dtoBaseline) != ExtractGeneratedRpcAssemblyHash(dtoChanged),
            "the DTO enum mapping change must propagate into RpcAssemblyHash");

        var dtoBaselineManifest = RunContractGenerator(dtoBaselineSource).Json;
        var dtoChangedManifest = RunContractGenerator(dtoChangedSource, dtoBaselineManifest);
        Ensure(
            dtoChangedManifest.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "swapping a DTO enum member's name/value mapping must fail contract baseline comparison");
        return Task.CompletedTask;
    }
}
