using System.Text.Json;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private const int ContractManifestFormatVersion = 3;
    private const string ContractManifestFormat = "SharpLink.Contracts";

    private static RpcUnionModel? GetUnionModelOrNull(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol)
            return null;
        var cases = symbol.GetAttributes()
            .Where(static attribute => IsAttribute(attribute, "SharpLink.Sdk", "RpcUnionCaseAttribute"))
            .Select(attribute =>
            {
                if (attribute.ConstructorArguments.Length != 2 ||
                    attribute.ConstructorArguments[0].Value is not int tag ||
                    attribute.ConstructorArguments[1].Value is not ITypeSymbol caseType)
                {
                    return null;
                }
                return new RpcUnionCaseModel(
                    tag,
                    RemoveGlobalPrefix(caseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                    GetInvalidUnionCaseDetail(symbol, tag, caseType, context.SemanticModel.Compilation),
                    attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? symbol.Locations.FirstOrDefault());
            })
            .Where(static item => item is not null)
            .Select(static item => item!)
            .OrderBy(static item => item.Tag)
            .ThenBy(static item => item.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();
        return cases.IsDefaultOrEmpty
            ? null
            : new RpcUnionModel(
                RemoveGlobalPrefix(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                cases,
                symbol.Locations.FirstOrDefault());
    }

    private static string? GetInvalidUnionCaseDetail(
        INamedTypeSymbol unionType,
        int tag,
        ITypeSymbol caseType,
        Compilation compilation)
    {
        if (tag <= 0)
            return $"union tag {tag} must be positive";
        if (caseType is not INamedTypeSymbol namedCase ||
            namedCase.IsUnboundGenericType ||
            HasTypeParameter(namedCase) ||
            namedCase.TypeKind is not (TypeKind.Class or TypeKind.Struct) ||
            namedCase.IsAbstract)
        {
            return $"case type '{RemoveGlobalPrefix(caseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))}' must be closed and concrete";
        }
        if (!IsDirectUnionCase(unionType, namedCase) &&
            (compilation is not Microsoft.CodeAnalysis.CSharp.CSharpCompilation csharpCompilation ||
             !csharpCompilation.ClassifyConversion(caseType, unionType).IsImplicit))
        {
            return $"case type '{RemoveGlobalPrefix(caseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))}' is not assignable to the annotated union";
        }
        return null;
    }

    private static bool IsDirectUnionCase(INamedTypeSymbol unionType, INamedTypeSymbol caseType)
    {
        if (SymbolEqualityComparer.Default.Equals(unionType, caseType))
            return true;
        if (unionType.TypeKind == TypeKind.Interface)
        {
            foreach (var candidate in caseType.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(candidate, unionType))
                    return true;
            }
        }
        for (var current = caseType.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, unionType))
                return true;
        }
        return false;
    }

    private static ContractManifestOptions GetContractManifestOptions(AnalyzerConfigOptionsProvider provider)
    {
        provider.GlobalOptions.TryGetValue(
            "build_property.SharpLinkContractBaseline", out var baselinePath);
        provider.GlobalOptions.TryGetValue(
            "build_property.SharpLinkContractManifestOutput", out var outputPath);
        return new ContractManifestOptions(baselinePath ?? string.Empty, outputPath ?? string.Empty);
    }

    private static ContractManifestAnalysis AnalyzeContractManifest(
        ImmutableArray<RpcInterfaceModel?> interfaces,
        ImmutableArray<RpcServiceModel?> services,
        ImmutableArray<GeneratedCodecModel> codecs,
        ImmutableArray<GeneratedCodecHashModel> codecHashes,
        ImmutableArray<GeneratedEnumModel> generatedEnums,
        ImmutableArray<RpcUnionModel?> unions,
        ImmutableArray<AdditionalText> additionalTexts,
        ContractManifestOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = CreateContractManifest(interfaces, services, codecs, codecHashes, generatedEnums, unions);
        var diagnostics = ValidateCurrentContractManifest(document);

        if (!string.IsNullOrWhiteSpace(options.BaselinePath))
        {
            var baselineFile = FindBaseline(additionalTexts, options.BaselinePath);
            if (baselineFile is null)
            {
                diagnostics.Add(new ContractCompatibilityDiagnostic(
                    ContractCompatibilityKind.BaselineInvalid,
                    Location.None,
                    options.BaselinePath,
                    "the configured file was not supplied as an AdditionalFile",
                    "ensure SharpLink.Sdk build assets are imported and the path exists"));
            }
            else
            {
                try
                {
                    var text = baselineFile.GetText(cancellationToken)?.ToString() ?? string.Empty;
                    var baseline = JsonSerializer.Deserialize<ContractManifestDocument>(text, ContractJsonOptions);
                    if (baseline is null || !string.Equals(baseline.Format, ContractManifestFormat, StringComparison.Ordinal))
                    {
                        diagnostics.Add(new ContractCompatibilityDiagnostic(
                            ContractCompatibilityKind.BaselineInvalid,
                            Location.None,
                            options.BaselinePath,
                            "the file is not a SharpLink contract Manifest",
                            "regenerate the baseline from a successful SharpLink build"));
                    }
                    else if (baseline.Version != ContractManifestFormatVersion)
                    {
                        diagnostics.Add(new ContractCompatibilityDiagnostic(
                            ContractCompatibilityKind.BaselineVersion,
                            Location.None,
                            options.BaselinePath,
                            $"format version {baseline.Version} is not supported by version {ContractManifestFormatVersion}",
                            "regenerate the baseline with the current SharpLink SDK"));
                    }
                    else if (!HasRequiredContractIdentities(baseline))
                    {
                        diagnostics.Add(new ContractCompatibilityDiagnostic(
                            ContractCompatibilityKind.BaselineInvalid,
                            Location.None,
                            options.BaselinePath,
                            "one or more Codec entries, enum entries, or opaque payload references are missing required semantic identity",
                            "regenerate the baseline with the current SharpLink SDK"));
                    }
                    else if (string.IsNullOrWhiteSpace(baseline.SchemaFingerprint) ||
                             !string.Equals(
                                 baseline.SchemaFingerprint,
                                 ComputeContractManifestFingerprint(baseline),
                                 StringComparison.Ordinal))
                    {
                        diagnostics.Add(new ContractCompatibilityDiagnostic(
                            ContractCompatibilityKind.BaselineInvalid,
                            Location.None,
                            options.BaselinePath,
                            "the schema fingerprint does not match the Manifest contents",
                            "restore the unmodified emitted baseline or regenerate it"));
                    }
                    else
                    {
                        diagnostics.AddRange(CompareContractManifests(baseline, document));
                    }
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException or
                    FormatException or InvalidOperationException or ArgumentException)
                {
                    diagnostics.Add(new ContractCompatibilityDiagnostic(
                        ContractCompatibilityKind.BaselineInvalid,
                        Location.None,
                        options.BaselinePath,
                        $"JSON parsing failed: {exception.Message}",
                        "replace the file with a Manifest emitted by a successful SharpLink build"));
                }
            }
        }

        return new ContractManifestAnalysis(
            JsonSerializer.Serialize(document, ContractJsonOptions) + "\n",
            options.OutputPath,
            diagnostics
                .Distinct()
                .OrderBy(static diagnostic => diagnostic.Kind)
                .ThenBy(static diagnostic => diagnostic.Item, StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static ContractManifestDocument CreateContractManifest(
        ImmutableArray<RpcInterfaceModel?> interfaces,
        ImmutableArray<RpcServiceModel?> services,
        ImmutableArray<GeneratedCodecModel> codecs,
        ImmutableArray<GeneratedCodecHashModel> codecHashes,
        ImmutableArray<GeneratedEnumModel> generatedEnums,
        ImmutableArray<RpcUnionModel?> unions)
    {
        var document = new ContractManifestDocument();
        var codecsByType = codecs
            .GroupBy(static codec => RemoveGlobalPrefix(codec.TypeName), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
        var codecHashesByType = codecHashes
            .GroupBy(static codec => RemoveGlobalPrefix(codec.TypeName), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => new RpcHashValue(group.First().High, group.First().Low).ToHex(),
                StringComparer.Ordinal);
        var opaqueCodecHashes = codecsByType
            .Where(static pair => pair.Value.Kind is GeneratedCodecKind.Custom or GeneratedCodecKind.Adapter)
            .ToDictionary(
                static pair => pair.Key,
                static pair => GetCodecHash(pair.Value),
                StringComparer.Ordinal);
        foreach (var contract in interfaces
                     .Where(static item => item is not null)
                     .Select(static item => item!)
                     .OrderBy(static item => item.Hash)
                     .ThenBy(static item => item.FullName, StringComparer.Ordinal))
        {
            var manifestContract = new ContractManifestContract
            {
                Name = RemoveGlobalPrefix(contract.FullName),
                Id = contract.Hash,
                Fingerprint = contract.Fingerprint,
                SourceLocation = contract.Location
            };
            foreach (var method in contract.Methods
                         .OrderBy(static item => item.Hash)
                         .ThenBy(static item => item.Name, StringComparer.Ordinal))
            {
                var manifestMethod = new ContractManifestMethod
                {
                    Name = method.Name,
                    Id = method.Hash,
                    Shape = GetMethodKind(method),
                    Fingerprint = method.Fingerprint,
                    SourceLocation = method.Location
                };
                foreach (var parameter in method.Parameters.Where(static parameter =>
                             !parameter.IsCancellationToken))
                {
                    var typeName = RemoveGlobalPrefix(parameter.IsStream
                        ? parameter.StreamItemType!
                        : parameter.Type);
                    manifestMethod.Request.Add(new ContractManifestValue
                    {
                        Name = parameter.Name,
                        Type = typeName,
                        WireType = GetContractWireType(typeName, parameter.IsStream
                            ? parameter.StreamItemEnumUnderlyingType
                            : parameter.EnumUnderlyingType),
                        CodecHash = GetOpaqueCodecHash(typeName, opaqueCodecHashes),
                        Nullable = parameter.PayloadNullable,
                        Stream = parameter.IsStream,
                        SourceLocation = parameter.Location
                    });
                }
                var responseType = method.IsStreamReturn
                    ? RemoveGlobalPrefix(method.StreamItemType!)
                    : method.IsVoid
                        ? "System.Void"
                        : RemoveGlobalPrefix(method.GenericArgumentType ?? method.ReturnType);
                manifestMethod.Response = new ContractManifestValue
                {
                    Name = "response",
                    Type = responseType,
                    WireType = GetContractWireType(
                        responseType,
                        method.IsStreamReturn
                            ? method.StreamItemEnumUnderlyingType
                            : method.ResponseEnumUnderlyingType),
                    CodecHash = GetOpaqueCodecHash(responseType, opaqueCodecHashes),
                    Nullable = method.ResponseNullable,
                    Stream = method.IsStreamReturn,
                    SourceLocation = method.Location
                };
                manifestContract.Methods.Add(manifestMethod);
            }
            document.Contracts.Add(manifestContract);
        }

        foreach (var codec in codecs
                     .Where(static item => item.Kind == GeneratedCodecKind.Dto && item.Location?.IsInSource == true)
                     .OrderBy(static item => item.TypeName, StringComparer.Ordinal))
        {
            var dto = new ContractManifestDto
            {
                Name = RemoveGlobalPrefix(codec.TypeName),
                Fingerprint = codec.SchemaId,
                SourceLocation = codec.Location
            };
            foreach (var member in codec.Members.OrderBy(static item => item.FieldId))
            {
                dto.Members.Add(new ContractManifestMember
                {
                    Name = member.Name,
                    Id = member.FieldId,
                    Type = RemoveGlobalPrefix(member.TypeName),
                    WireType = GetMemberWireType(member),
                    CodecHash = GetOpaqueCodecHash(member.TypeName, opaqueCodecHashes),
                    Nullable = member.Nullable,
                    Required = member.Required,
                    ExplicitId = member.HasExplicitId,
                    SourceLocation = member.Location
                });
            }
            document.Dtos.Add(dto);
        }

        foreach (var codec in codecs.OrderBy(static item => item.TypeName, StringComparer.Ordinal))
        {
            document.Codecs.Add(new ContractManifestCodec
            {
                Type = RemoveGlobalPrefix(codec.TypeName),
                Kind = codec.Kind.ToString(),
                CodecHash = GetCodecHash(codec),
                SourceLocation = codec.Location
            });
        }

        var enums = new Dictionary<string, ContractManifestEnum>(StringComparer.Ordinal);
        void AddEnum(string? name, string? underlying, Location? location)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(underlying))
                return;
            name = RemoveGlobalPrefix(name!);
            if (!codecHashesByType.TryGetValue(name, out var codecHash))
            {
                throw new InvalidOperationException(
                    $"Final RPC Codec graph is missing enum CodecHash metadata for '{name}'.");
            }
            if (!enums.ContainsKey(name))
            {
                enums.Add(name, new ContractManifestEnum
                {
                    Name = name,
                    UnderlyingType = RemoveGlobalPrefix(underlying!),
                    CodecHash = codecHash,
                    SourceLocation = location
                });
            }
        }
        foreach (var contract in interfaces.Where(static item => item is not null).Select(static item => item!))
        {
            foreach (var method in contract.Methods)
            {
                foreach (var parameter in method.Parameters)
                {
                    AddEnum(parameter.IsStream ? parameter.StreamItemType : parameter.Type,
                        parameter.IsStream ? parameter.StreamItemEnumUnderlyingType : parameter.EnumUnderlyingType,
                        parameter.Location);
                }
                AddEnum(method.IsStreamReturn ? method.StreamItemType : method.GenericArgumentType,
                    method.IsStreamReturn ? method.StreamItemEnumUnderlyingType : method.ResponseEnumUnderlyingType,
                    method.Location);
            }
        }
        foreach (var codec in codecs)
        {
            foreach (var member in codec.Members)
                AddEnum(member.TypeName, member.EnumUnderlyingType, member.Location);
        }
        foreach (var item in generatedEnums)
            AddEnum(item.TypeName, item.UnderlyingType, item.Location);
        document.Enums.AddRange(enums.Values.OrderBy(static item => item.Name, StringComparer.Ordinal));

        foreach (var union in unions
                     .Where(static item => item is not null)
                     .Select(static item => item!)
                     .OrderBy(static item => item.TypeName, StringComparer.Ordinal))
        {
            var manifestUnion = new ContractManifestUnion
            {
                Name = union.TypeName,
                SourceLocation = union.Location
            };
            foreach (var item in union.Cases)
            {
                manifestUnion.Cases.Add(new ContractManifestUnionCase
                {
                    Tag = item.Tag,
                    Type = item.TypeName,
                    InvalidDetail = item.InvalidDetail,
                    SourceLocation = item.Location
                });
            }
            document.Unions.Add(manifestUnion);
        }

        foreach (var service in services
                     .Where(static item => item is not null)
                     .Select(static item => item!)
                     .OrderBy(static item => item.Interface.Hash)
                     .ThenBy(static item => item.ServiceFullName, StringComparer.Ordinal))
        {
            document.Services.Add(new ContractManifestService
            {
                ContractId = service.Interface.Hash,
                ContractName = RemoveGlobalPrefix(service.Interface.FullName),
                Implementation = RemoveGlobalPrefix(service.ServiceFullName),
                SourceLocation = service.Location
            });
        }

        document.SchemaFingerprint = ComputeContractManifestFingerprint(document);
        return document;
    }
}
