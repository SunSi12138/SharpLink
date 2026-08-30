namespace SharpLink.Generator;

public partial class RpcGenerator
{
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
            foreach (var item in union.Cases.Where(static item => item.InvalidDetail is not null))
            {
                diagnostics.Add(new ContractCompatibilityDiagnostic(
                    ContractCompatibilityKind.UnionDeclaration,
                    item.SourceLocation,
                    union.Name,
                    item.InvalidDetail!,
                    "use a positive tag and a closed concrete case type assignable to the annotated union"));
            }
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
            foreach (var group in union.Cases
                         .Where(static item => item.InvalidDetail is null)
                         .GroupBy(static item => item.Type, StringComparer.Ordinal)
                         .Where(static group => group.Select(static item => item.Tag).Distinct().Count() > 1))
            {
                foreach (var item in group.OrderBy(static item => item.Tag).Skip(1))
                {
                    diagnostics.Add(new ContractCompatibilityDiagnostic(
                        ContractCompatibilityKind.UnionDeclaration,
                        item.SourceLocation,
                        union.Name,
                        $"case type '{item.Type}' is already assigned to tag {group.Min(static candidate => candidate.Tag)}",
                        "assign each concrete case type to exactly one stable tag"));
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
                    {
                        diagnostics.Add(Change(
                            ContractCompatibilityKind.ContractRemoved,
                            Location.None,
                            oldContract.Name,
                            $"existing contract ID {oldContract.Id} and all of its routes were removed",
                            "restore the contract and deprecate it without removing its published routes"));
                        continue;
                    }
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
                        !string.Equals(oldMember.WireType, newMember.WireType, StringComparison.Ordinal) ||
                        !string.Equals(oldMember.CodecHash, newMember.CodecHash, StringComparison.Ordinal))
                    {
                        diagnostics.Add(Change(
                            ContractCompatibilityKind.WireType,
                            newMember.SourceLocation,
                            $"{newDto.Name}.{newMember.Name}",
                            $"member {oldMember.Id} changed from {oldMember.Type}/{oldMember.WireType}/{oldMember.CodecHash} to {newMember.Type}/{newMember.WireType}/{newMember.CodecHash}",
                            "restore the old wire type or semantic Codec identity, or add a new optional member ID"));
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

        var directlyDescribedCodecTypes = new HashSet<string>(
            baseline.Contracts
                .SelectMany(static contract => contract.Methods)
                .SelectMany(static method => method.Request.Append(method.Response))
                .Select(static value => value.Type)
                .Concat(baseline.Dtos.SelectMany(static dto => dto.Members).Select(static member => member.Type)),
            StringComparer.Ordinal);
        var currentCodecs = current.Codecs.ToDictionary(static codec => codec.Type, StringComparer.Ordinal);
        foreach (var oldCodec in baseline.Codecs)
        {
            if (!currentCodecs.TryGetValue(oldCodec.Type, out var newCodec))
                continue;

            var opaque =
                string.Equals(oldCodec.Kind, "Custom", StringComparison.Ordinal) ||
                string.Equals(oldCodec.Kind, "Adapter", StringComparison.Ordinal) ||
                string.Equals(newCodec.Kind, "Custom", StringComparison.Ordinal) ||
                string.Equals(newCodec.Kind, "Adapter", StringComparison.Ordinal);
            if (!opaque || string.Equals(oldCodec.CodecHash, newCodec.CodecHash, StringComparison.Ordinal))
                continue;
            if (directlyDescribedCodecTypes.Contains(oldCodec.Type))
                continue;

            diagnostics.Add(Change(
                ContractCompatibilityKind.WireType,
                newCodec.SourceLocation,
                oldCodec.Type,
                $"nested CodecHash changed from '{oldCodec.CodecHash}' to '{newCodec.CodecHash}'",
                "restore the previous semantic Codec identity or add a new RPC payload type"));
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

        var currentServiceContractIds = new HashSet<long>(
            current.Services.Select(static service => service.ContractId));
        foreach (var oldService in baseline.Services
                     .GroupBy(static service => service.ContractId)
                     .Select(static group => group.First()))
        {
            if (currentServiceContractIds.Contains(oldService.ContractId))
                continue;
            var location = current.Contracts
                .FirstOrDefault(contract => contract.Id == oldService.ContractId)?.SourceLocation;
            diagnostics.Add(Change(
                ContractCompatibilityKind.ServiceRouteRemoved,
                location,
                oldService.ContractName,
                $"service route for contract ID {oldService.ContractId} no longer has an [RpcService] implementation",
                "restore a service implementation for the published contract route"));
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
                !string.Equals(oldValue.CodecHash, newValue.CodecHash, StringComparison.Ordinal) ||
                oldValue.Stream != newValue.Stream ||
                oldValue.Nullable != newValue.Nullable)
            {
                diagnostics.Add(Change(
                    ContractCompatibilityKind.WireType,
                    newValue.SourceLocation ?? fallbackLocation,
                    item,
                    $"element {index} changed from {oldValue.Type}/{oldValue.WireType}/{oldValue.CodecHash}/nullable={oldValue.Nullable} to {newValue.Type}/{newValue.WireType}/{newValue.CodecHash}/nullable={newValue.Nullable}",
                    "restore the previous type, wire framing, or semantic Codec identity, or add a new method route"));
            }
        }
    }
}
