using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private const int ContractManifestFormatVersion = 1;
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
        ImmutableArray<RpcUnionModel?> unions,
        ImmutableArray<AdditionalText> additionalTexts,
        ContractManifestOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = CreateContractManifest(interfaces, services, codecs, unions);
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
        ImmutableArray<RpcUnionModel?> unions)
    {
        var document = new ContractManifestDocument();
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
                             !parameter.IsCancellationToken && !parameter.IsCallOptions))
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
                    Nullable = member.Nullable,
                    Required = member.Required,
                    ExplicitId = member.HasExplicitId,
                    SourceLocation = member.Location
                });
            }
            document.Dtos.Add(dto);
        }

        var enums = new Dictionary<string, ContractManifestEnum>(StringComparer.Ordinal);
        void AddEnum(string? name, string? underlying, Location? location)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(underlying))
                return;
            name = RemoveGlobalPrefix(name!);
            if (!enums.ContainsKey(name))
            {
                enums.Add(name, new ContractManifestEnum
                {
                    Name = name,
                    UnderlyingType = RemoveGlobalPrefix(underlying!),
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

    private static List<ContractCompatibilityDiagnostic> ValidateCurrentContractManifest(
        ContractManifestDocument current)
    {
        var diagnostics = new List<ContractCompatibilityDiagnostic>();
        foreach (var group in current.Contracts.GroupBy(static item => item.Id).Where(static group => group.Count() > 1))
        {
            foreach (var contract in group.Skip(1))
            {
                diagnostics.Add(new ContractCompatibilityDiagnostic(
                    ContractCompatibilityKind.ContractId,
                    contract.SourceLocation,
                    contract.Name,
                    $"contract ID {group.Key} is already used by '{group.First().Name}'",
                    "assign unique contract names or explicit stable IDs"));
            }
        }
        foreach (var contract in current.Contracts)
        {
            foreach (var group in contract.Methods.GroupBy(static item => item.Id).Where(static group => group.Count() > 1))
            {
                foreach (var method in group.Skip(1))
                {
                    diagnostics.Add(new ContractCompatibilityDiagnostic(
                        ContractCompatibilityKind.MethodId,
                        method.SourceLocation,
                        $"{contract.Name}.{method.Name}",
                        $"method ID {group.Key} is already used by '{group.First().Name}'",
                        "change the signature so every RPC route has a unique stable ID"));
                }
            }
        }
        foreach (var union in current.Unions)
        {
            foreach (var group in union.Cases.GroupBy(static item => item.Tag).Where(static group => group.Count() > 1))
            {
                foreach (var item in group.Skip(1))
                {
                    diagnostics.Add(new ContractCompatibilityDiagnostic(
                        ContractCompatibilityKind.UnionTag,
                        item.SourceLocation,
                        union.Name,
                        $"union tag {group.Key} is already assigned to '{group.First().Type}'",
                        "allocate a unique tag for every union case"));
                }
            }
        }
        return diagnostics;
    }

    private static IEnumerable<ContractCompatibilityDiagnostic> CompareContractManifests(
        ContractManifestDocument baseline,
        ContractManifestDocument current)
    {
        var diagnostics = new List<ContractCompatibilityDiagnostic>();
        var currentContractsById = current.Contracts
            .GroupBy(static item => item.Id)
            .ToDictionary(static group => group.Key, static group => group.First());
        var currentContractsByName = current.Contracts
            .GroupBy(static item => item.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        foreach (var oldContract in baseline.Contracts)
        {
            if (!currentContractsById.TryGetValue(oldContract.Id, out var newContract))
            {
                if (currentContractsByName.TryGetValue(oldContract.Name, out newContract))
                {
                    diagnostics.Add(Change(
                        ContractCompatibilityKind.ContractId,
                        newContract.SourceLocation,
                        oldContract.Name,
                        $"contract ID changed from {oldContract.Id} to {newContract.Id}",
                        "restore the original contract name/ID or publish a new contract"));
                }
                else
                {
                    var renameCandidates = current.Contracts
                        .Where(candidate => candidate.Methods.Count == oldContract.Methods.Count &&
                                            candidate.Methods.Select(static method => method.Name)
                                                .SequenceEqual(oldContract.Methods.Select(static method => method.Name)))
                        .Take(2)
                        .ToArray();
                    if (renameCandidates.Length != 1)
                        continue;
                    newContract = renameCandidates[0];
                    diagnostics.Add(Change(
                        ContractCompatibilityKind.ContractId,
                        newContract.SourceLocation,
                        newContract.Name,
                        $"contract '{oldContract.Name}' changed ID from {oldContract.Id} to {newContract.Id} after renaming",
                        "restore the original contract identity or add a separate new contract"));
                }
            }

            var currentMethodsById = newContract.Methods
                .GroupBy(static item => item.Id)
                .ToDictionary(static group => group.Key, static group => group.First());
            var currentMethodsByName = newContract.Methods
                .GroupBy(static item => item.Name, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
            foreach (var oldMethod in oldContract.Methods)
            {
                if (!currentMethodsById.TryGetValue(oldMethod.Id, out var newMethod))
                {
                    if (currentMethodsByName.TryGetValue(oldMethod.Name, out newMethod))
                    {
                        diagnostics.Add(Change(
                            ContractCompatibilityKind.MethodId,
                            newMethod.SourceLocation,
                            $"{newContract.Name}.{newMethod.Name}",
                            $"method ID changed from {oldMethod.Id} to {newMethod.Id}",
                            "restore the previous signature/ID or add a new method instead"));
                    }
                    else
                    {
                        diagnostics.Add(Change(
                            ContractCompatibilityKind.MethodRemoved,
                            newContract.SourceLocation,
                            $"{oldContract.Name}.{oldMethod.Name}",
                            $"existing method ID {oldMethod.Id} was removed",
                            "restore the method and deprecate it without removing its route"));
                        continue;
                    }
                }
                if (!string.Equals(oldMethod.Shape, newMethod.Shape, StringComparison.Ordinal))
                {
                    diagnostics.Add(Change(
                        ContractCompatibilityKind.CallShape,
                        newMethod.SourceLocation,
                        $"{newContract.Name}.{newMethod.Name}",
                        $"RPC shape changed from {oldMethod.Shape} to {newMethod.Shape}",
                        "add a new method for the new Unary/Streaming shape"));
                }
                CompareValues(oldMethod.Request, newMethod.Request,
                    $"{newContract.Name}.{newMethod.Name} request", newMethod.SourceLocation, diagnostics);
                CompareValues([oldMethod.Response], [newMethod.Response],
                    $"{newContract.Name}.{newMethod.Name} response", newMethod.SourceLocation, diagnostics);
            }
        }

        var currentDtos = current.Dtos.ToDictionary(static item => item.Name, StringComparer.Ordinal);
        foreach (var oldDto in baseline.Dtos)
        {
            if (!currentDtos.TryGetValue(oldDto.Name, out var newDto))
                continue;
            var newById = newDto.Members.ToDictionary(static item => item.Id);
            var newByName = newDto.Members.ToDictionary(static item => item.Name, StringComparer.Ordinal);
            var matchedNewIds = new HashSet<uint>();
            foreach (var oldMember in oldDto.Members)
            {
                if (newById.TryGetValue(oldMember.Id, out var newMember))
                {
                    matchedNewIds.Add(newMember.Id);
                    if (!string.Equals(oldMember.Type, newMember.Type, StringComparison.Ordinal) ||
                        !string.Equals(oldMember.WireType, newMember.WireType, StringComparison.Ordinal))
                    {
                        diagnostics.Add(Change(
                            ContractCompatibilityKind.WireType,
                            newMember.SourceLocation,
                            $"{newDto.Name}.{newMember.Name}",
                            $"member {oldMember.Id} changed from {oldMember.Type}/{oldMember.WireType} to {newMember.Type}/{newMember.WireType}",
                            "restore the old wire type or add a new optional member ID"));
                    }
                    if (!oldMember.Required && newMember.Required)
                    {
                        diagnostics.Add(Change(
                            ContractCompatibilityKind.Required,
                            newMember.SourceLocation,
                            $"{newDto.Name}.{newMember.Name}",
                            $"existing member {oldMember.Id} became required",
                            "keep the field optional and enforce requirements in application code"));
                    }
                    continue;
                }

                if (newByName.TryGetValue(oldMember.Name, out newMember))
                {
                    matchedNewIds.Add(newMember.Id);
                    diagnostics.Add(Change(
                        ContractCompatibilityKind.MemberId,
                        newMember.SourceLocation,
                        $"{newDto.Name}.{newMember.Name}",
                        $"member ID changed from {oldMember.Id} to {newMember.Id}",
                        $"annotate the member with [RpcMember({oldMember.Id})]"));
                    continue;
                }

                var renamed = newDto.Members
                    .Where(candidate => !matchedNewIds.Contains(candidate.Id) && !candidate.ExplicitId)
                    .Where(candidate => string.Equals(candidate.Type, oldMember.Type, StringComparison.Ordinal) &&
                                        string.Equals(candidate.WireType, oldMember.WireType, StringComparison.Ordinal) &&
                                        candidate.Required == oldMember.Required)
                    .Take(2)
                    .ToArray();
                if (renamed.Length == 1)
                {
                    matchedNewIds.Add(renamed[0].Id);
                    diagnostics.Add(Change(
                        ContractCompatibilityKind.MemberId,
                        renamed[0].SourceLocation,
                        $"{newDto.Name}.{renamed[0].Name}",
                        $"renaming '{oldMember.Name}' changed the default member ID {oldMember.Id} to {renamed[0].Id}",
                        $"annotate the renamed member with [RpcMember({oldMember.Id})]"));
                }
                else if (oldMember.Required)
                {
                    diagnostics.Add(Change(
                        ContractCompatibilityKind.Required,
                        newDto.SourceLocation,
                        $"{oldDto.Name}.{oldMember.Name}",
                        $"required member {oldMember.Id} was removed",
                        "restore the required member or introduce a new DTO version"));
                }
            }

            var oldIds = new HashSet<uint>(oldDto.Members.Select(static item => item.Id));
            foreach (var newMember in newDto.Members.Where(item => !oldIds.Contains(item.Id) && item.Required))
            {
                diagnostics.Add(Change(
                    ContractCompatibilityKind.Required,
                    newMember.SourceLocation,
                    $"{newDto.Name}.{newMember.Name}",
                    $"new member {newMember.Id} is required",
                    "make the new member optional so older payloads remain readable"));
            }
        }

        var currentEnums = current.Enums.ToDictionary(static item => item.Name, StringComparer.Ordinal);
        foreach (var oldEnum in baseline.Enums)
        {
            if (currentEnums.TryGetValue(oldEnum.Name, out var newEnum) &&
                !string.Equals(oldEnum.UnderlyingType, newEnum.UnderlyingType, StringComparison.Ordinal))
            {
                diagnostics.Add(Change(
                    ContractCompatibilityKind.EnumUnderlyingType,
                    newEnum.SourceLocation,
                    newEnum.Name,
                    $"enum underlying type changed from {oldEnum.UnderlyingType} to {newEnum.UnderlyingType}",
                    "restore the original enum underlying type"));
            }
        }

        var currentUnions = current.Unions.ToDictionary(static item => item.Name, StringComparer.Ordinal);
        foreach (var oldUnion in baseline.Unions)
        {
            if (!currentUnions.TryGetValue(oldUnion.Name, out var newUnion))
                continue;
            var currentCases = newUnion.Cases.ToDictionary(static item => item.Tag);
            foreach (var oldCase in oldUnion.Cases)
            {
                if (currentCases.TryGetValue(oldCase.Tag, out var newCase) &&
                    !string.Equals(oldCase.Type, newCase.Type, StringComparison.Ordinal))
                {
                    diagnostics.Add(Change(
                        ContractCompatibilityKind.UnionTag,
                        newCase.SourceLocation,
                        newUnion.Name,
                        $"union tag {oldCase.Tag} was reassigned from {oldCase.Type} to {newCase.Type}",
                        "restore the original mapping and allocate a new tag"));
                }
            }
        }
        return diagnostics;
    }

    private static void CompareValues(
        IReadOnlyList<ContractManifestValue> baseline,
        IReadOnlyList<ContractManifestValue> current,
        string item,
        Location? fallbackLocation,
        List<ContractCompatibilityDiagnostic> diagnostics)
    {
        if (baseline.Count != current.Count)
        {
            diagnostics.Add(Change(
                ContractCompatibilityKind.WireType,
                fallbackLocation,
                item,
                $"payload element count changed from {baseline.Count} to {current.Count}",
                "add a new method route for the new payload shape"));
            return;
        }
        for (var index = 0; index < baseline.Count; index++)
        {
            var oldValue = baseline[index];
            var newValue = current[index];
            if (!string.Equals(oldValue.Type, newValue.Type, StringComparison.Ordinal) ||
                !string.Equals(oldValue.WireType, newValue.WireType, StringComparison.Ordinal) ||
                oldValue.Stream != newValue.Stream ||
                oldValue.Nullable != newValue.Nullable)
            {
                diagnostics.Add(Change(
                    ContractCompatibilityKind.WireType,
                    newValue.SourceLocation ?? fallbackLocation,
                    item,
                    $"element {index} changed from {oldValue.Type}/{oldValue.WireType}/nullable={oldValue.Nullable} to {newValue.Type}/{newValue.WireType}/nullable={newValue.Nullable}",
                    "restore the previous type or add a new method route"));
            }
        }
    }

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
        MethodRemoved,
        ManifestOutput
    }

    private sealed class ContractManifestDocument
    {
        public string Format { get; set; } = ContractManifestFormat;
        public int Version { get; set; } = ContractManifestFormatVersion;
        public string GeneratorVersion { get; set; } = "0.7.3";
        public string SchemaFingerprint { get; set; } = string.Empty;
        public List<ContractManifestContract> Contracts { get; set; } = [];
        public List<ContractManifestDto> Dtos { get; set; } = [];
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

    private sealed class ContractManifestMember
    {
        public string Name { get; set; } = string.Empty;
        public uint Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string WireType { get; set; } = string.Empty;
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
