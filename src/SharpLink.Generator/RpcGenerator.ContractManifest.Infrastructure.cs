using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static bool HasRequiredContractIdentities(ContractManifestDocument manifest)
    {
        if (manifest.Contracts is null ||
            manifest.Dtos is null ||
            manifest.Codecs is null ||
            manifest.Enums is null ||
            manifest.Unions is null ||
            manifest.Services is null)
        {
            return false;
        }

        var opaqueCodecTypes = new HashSet<string>(
            manifest.Codecs
                .Where(static codec => codec is not null &&
                    (string.Equals(codec.Kind, "Custom", StringComparison.Ordinal) ||
                     string.Equals(codec.Kind, "Adapter", StringComparison.Ordinal)) &&
                    IsValidCodecHash(codec.CodecHash))
                .Select(static codec => codec.Type),
            StringComparer.Ordinal);

        bool HasValueIdentity(string type, string? codecHash)
            => !opaqueCodecTypes.Contains(type) || IsValidCodecHash(codecHash);

        return manifest.Contracts.All(contract =>
                   contract is not null &&
                   contract.Methods is not null &&
                   contract.Methods.All(method =>
                       method is not null &&
                       method.Request is not null &&
                       method.Response is not null &&
                       method.Request.All(value =>
                           value is not null && HasValueIdentity(value.Type, value.CodecHash)) &&
                       HasValueIdentity(method.Response.Type, method.Response.CodecHash))) &&
               manifest.Dtos.All(dto =>
                   dto is not null &&
                   dto.Members is not null &&
                   dto.Members.All(member =>
                       member is not null && HasValueIdentity(member.Type, member.CodecHash))) &&
               manifest.Codecs.All(static codec =>
                   codec is not null &&
                   !string.IsNullOrWhiteSpace(codec.Type) &&
                   !string.IsNullOrWhiteSpace(codec.Kind) &&
                   IsValidCodecHash(codec.CodecHash)) &&
               manifest.Enums.All(static item => item is not null) &&
               manifest.Unions.All(static union =>
                   union is not null && union.Cases is not null && union.Cases.All(static item => item is not null)) &&
               manifest.Services.All(static service => service is not null);
    }

    private static bool IsValidCodecHash(string? value)
    {
        if (value is null || value.Length != 32)
            return false;
        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    private static string GetCodecHash(GeneratedCodecModel codec)
        => new RpcHashValue(codec.CodecHashHigh, codec.CodecHashLow).ToHex();

    private static string? GetOpaqueCodecHash(
        string typeName,
        IReadOnlyDictionary<string, string> opaqueCodecHashes)
        => opaqueCodecHashes.TryGetValue(RemoveGlobalPrefix(typeName), out var codecHash)
            ? codecHash
            : null;

    private static ContractCompatibilityDiagnostic Change(
        ContractCompatibilityKind kind,
        Location? location,
        string item,
        string detail,
        string fix)
        => new(kind, location ?? Location.None, item, detail, fix);

    private static AdditionalText? FindBaseline(ImmutableArray<AdditionalText> files, string configuredPath)
    {
        string expected;
        try
        {
            expected = Path.GetFullPath(configuredPath);
        }
        catch
        {
            expected = configuredPath;
        }
        foreach (var file in files)
        {
            string actual;
            try
            {
                actual = Path.GetFullPath(file.Path);
            }
            catch
            {
                actual = file.Path;
            }
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }

    private static string ComputeContractManifestFingerprint(ContractManifestDocument document)
    {
        var fingerprint = document.SchemaFingerprint;
        document.SchemaFingerprint = string.Empty;
        var canonical = JsonSerializer.Serialize(document, ContractJsonOptions);
        document.SchemaFingerprint = fingerprint;
        return Hashing.GetSha256(canonical);
    }

    private static string GetMemberWireType(GeneratedMemberModel member)
        => member.Kind == GeneratedMemberKind.Complex || member.Kind == GeneratedMemberKind.String
            ? "LengthDelimited"
            : member.FixedSize switch
            {
                1 => "Fixed1",
                2 => "Fixed2",
                4 => "Fixed4",
                8 => "Fixed8",
                16 => "Fixed16",
                _ => "LengthDelimited"
            };

    private static string GetContractWireType(string typeName, string? enumUnderlyingType)
    {
        var type = RemoveGlobalPrefix(enumUnderlyingType ?? typeName);
        return type switch
        {
            "System.Void" => "None",
            "bool" or "byte" or "sbyte" or "System.Boolean" or "System.Byte" or "System.SByte" => "Fixed1",
            "short" or "ushort" or "char" or "System.Int16" or "System.UInt16" or "System.Char" or "System.Half" => "Fixed2",
            "int" or "uint" or "float" or "System.Int32" or "System.UInt32" or "System.Single" or
                "System.Text.Rune" or "System.Index" or "System.DateOnly" => "Fixed4",
            "long" or "ulong" or "double" or "System.Int64" or "System.UInt64" or "System.Double" or
                "System.Range" or "System.DateTime" or "System.TimeOnly" or "System.TimeSpan" => "Fixed8",
            "decimal" or "System.Decimal" or "System.Guid" or "System.DateTimeOffset" or
                "System.Int128" or "System.UInt128" => "Fixed16",
            _ => "LengthDelimited"
        };
    }

#pragma warning disable RS1035 // The opt-in SDK output path is the requested CI artifact boundary.
    private static void WriteContractManifest(string outputPath, string json)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return;
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(fullPath) && string.Equals(File.ReadAllText(fullPath), json, StringComparison.Ordinal))
            return;
        File.WriteAllText(fullPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
#pragma warning restore RS1035

    private static string GenerateContractManifestSource(string json)
    {
        var escaped = json.Replace("\"", "\"\"");
        return $$"""
// <auto-generated/>
#nullable enable
namespace SharpLink.Generated;

[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
internal static class __SharpLinkContractManifest
{
    internal const string Json = @"{{escaped}}";
}
""";
    }

    private static readonly JsonSerializerOptions ContractJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly record struct ContractManifestOptions(string BaselinePath, string OutputPath);

    private sealed record ContractManifestAnalysis(
        string Json,
        string OutputPath,
        ImmutableArray<ContractCompatibilityDiagnostic> Diagnostics);

    private sealed record ContractManifestModels(
        ImmutableArray<RpcInterfaceModel?> Interfaces,
        ImmutableArray<RpcServiceModel?> Services,
        ImmutableArray<GeneratedCodecModel> Codecs,
        ImmutableArray<GeneratedEnumModel> Enums,
        ImmutableArray<RpcUnionModel?> Unions);

    private readonly record struct ContractCompatibilityDiagnostic(
        ContractCompatibilityKind Kind,
        Location? Location,
        string Item,
        string Detail,
        string Fix);

    private enum ContractCompatibilityKind
    {
        BaselineInvalid,
        BaselineVersion,
        ContractId,
        MethodId,
        MemberId,
        CallShape,
        WireType,
        Required,
        EnumUnderlyingType,
        UnionTag,
        UnionDeclaration,
        MethodRemoved,
        ContractRemoved,
        ServiceRouteRemoved,
        ManifestOutput
    }

    private sealed class ContractManifestDocument
    {
        public string Format { get; set; } = ContractManifestFormat;
        public int Version { get; set; } = ContractManifestFormatVersion;
        public string GeneratorVersion { get; set; } = ExecutingGeneratorVersion;
        public string SchemaFingerprint { get; set; } = string.Empty;
        public List<ContractManifestContract> Contracts { get; set; } = [];
        public List<ContractManifestDto> Dtos { get; set; } = [];
        [JsonRequired]
        public List<ContractManifestCodec> Codecs { get; set; } = [];
        public List<ContractManifestEnum> Enums { get; set; } = [];
        public List<ContractManifestUnion> Unions { get; set; } = [];
        public List<ContractManifestService> Services { get; set; } = [];
    }

    private sealed class ContractManifestContract
    {
        public string Name { get; set; } = string.Empty;
        public long Id { get; set; }
        public string Fingerprint { get; set; } = string.Empty;
        public List<ContractManifestMethod> Methods { get; set; } = [];
        [JsonIgnore] public Location? SourceLocation { get; set; }
    }

    private sealed class ContractManifestMethod
    {
        public string Name { get; set; } = string.Empty;
        public long Id { get; set; }
        public string Shape { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public List<ContractManifestValue> Request { get; set; } = [];
        public ContractManifestValue Response { get; set; } = new();
        [JsonIgnore] public Location? SourceLocation { get; set; }
    }

    private sealed class ContractManifestValue
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string WireType { get; set; } = string.Empty;
        public string? CodecHash { get; set; }
        public bool Nullable { get; set; }
        public bool Stream { get; set; }
        [JsonIgnore] public Location? SourceLocation { get; set; }
    }

    private sealed class ContractManifestDto
    {
        public string Name { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public List<ContractManifestMember> Members { get; set; } = [];
        [JsonIgnore] public Location? SourceLocation { get; set; }
    }

    private sealed class ContractManifestCodec
    {
        public string Type { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string CodecHash { get; set; } = string.Empty;
        [JsonIgnore] public Location? SourceLocation { get; set; }
    }

    private sealed class ContractManifestMember
    {
        public string Name { get; set; } = string.Empty;
        public uint Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string WireType { get; set; } = string.Empty;
        public string? CodecHash { get; set; }
        public bool Nullable { get; set; }
        public bool Required { get; set; }
        public bool ExplicitId { get; set; }
        [JsonIgnore] public Location? SourceLocation { get; set; }
    }

    private sealed class ContractManifestEnum
    {
        public string Name { get; set; } = string.Empty;
        public string UnderlyingType { get; set; } = string.Empty;
        [JsonIgnore] public Location? SourceLocation { get; set; }
    }

    private sealed class ContractManifestUnion
    {
        public string Name { get; set; } = string.Empty;
        public List<ContractManifestUnionCase> Cases { get; set; } = [];
        [JsonIgnore] public Location? SourceLocation { get; set; }
    }

    private sealed class ContractManifestUnionCase
    {
        public int Tag { get; set; }
        public string Type { get; set; } = string.Empty;
        [JsonIgnore] public string? InvalidDetail { get; set; }
        [JsonIgnore] public Location? SourceLocation { get; set; }
    }

    private sealed class ContractManifestService
    {
        public long ContractId { get; set; }
        public string ContractName { get; set; } = string.Empty;
        public string Implementation { get; set; } = string.Empty;
        [JsonIgnore] public Location? SourceLocation { get; set; }
    }
}
