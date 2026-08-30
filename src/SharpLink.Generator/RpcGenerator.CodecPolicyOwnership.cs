namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static DtoGenerationResult AnalyzeGeneratedCodecsWithPolicyOwnership(
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var standaloneState = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: false,
            applyCodecPolicy: true,
            selectorOnlyContractDefault: false);
        var standalone = standaloneState.AnalyzeWithFinalCodecBindings();
        var standaloneModels = standaloneState.FilterFailedCodecClosure(standalone.Codecs);
        var standaloneHashes = standaloneState.BuildFinalCodecHashes(
            includeSerializable: true,
            includeContracts: false);
        var standaloneCodecs = AttachCodecHashes(standaloneModels, standaloneHashes);

        var contractDefaultState = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: true,
            applyCodecPolicy: true,
            selectorOnlyContractDefault: true);
        var contractDefault = contractDefaultState.AnalyzeWithFinalCodecBindings();
        var contractDefaultModels = contractDefaultState.FilterFailedCodecClosure(contractDefault.Codecs);
        var contractDefaultHashes = contractDefaultState.BuildFinalCodecHashes(
            includeSerializable: false,
            includeContracts: true);
        var contractDefaultCodecs = AttachCodecHashes(contractDefaultModels, contractDefaultHashes);

        var contractPolicyState = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: true,
            applyCodecPolicy: true,
            selectorOnlyContractDefault: false);
        var contractPolicy = contractPolicyState.AnalyzeWithFinalCodecBindings();
        var contractPolicyModels = contractPolicyState.FilterFailedCodecClosure(contractPolicy.Codecs);
        var codecHashes = contractPolicyState.BuildFinalCodecHashes(
            includeSerializable: false,
            includeContracts: true);
        var contractPolicyCodecs = AttachCodecHashes(contractPolicyModels, codecHashes);

        var currentContractTypes = contractPolicyState.GetCurrentContractReachableTypeNames();
        var currentContractDefaultCodecs = contractDefaultCodecs
            .Where(codec => currentContractTypes.Contains(codec.TypeName))
            .ToImmutableArray();
        var currentContractPolicyCodecs = contractPolicyCodecs
            .Where(codec => currentContractTypes.Contains(codec.TypeName))
            .ToImmutableArray();
        var contractOwnedPolicyRoots = new HashSet<string>(
            contractPolicyState.ContractOwnedPolicyRoots.Where(currentContractTypes.Contains),
            StringComparer.Ordinal);

        var standaloneTypes = new HashSet<string>(
            standaloneCodecs.Select(static codec => codec.TypeName),
            StringComparer.Ordinal);
        var defaultByType = currentContractDefaultCodecs
            .ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        var policyByType = currentContractPolicyCodecs
            .ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        var globalExcludedTypes = new HashSet<string>(
            contractOwnedPolicyRoots.Where(type =>
                !standaloneTypes.Contains(type) &&
                policyByType.TryGetValue(type, out var policyCodec) &&
                (!defaultByType.TryGetValue(type, out var defaultCodec) ||
                 !HasSameFinalCodecBinding(defaultCodec, policyCodec))),
            StringComparer.Ordinal);
        ExpandReverseCodecDependencyClosure(currentContractDefaultCodecs, globalExcludedTypes);
        var globalByType = currentContractDefaultCodecs
            .Where(codec => !globalExcludedTypes.Contains(codec.TypeName))
            .ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        foreach (var codec in standaloneCodecs)
            globalByType[codec.TypeName] = codec;
        var globalCodecs = globalByType.Values
            .OrderBy(static codec => codec.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        var contractCodecs = SelectOwnedContractCodecs(
            currentContractDefaultCodecs,
            currentContractPolicyCodecs,
            contractOwnedPolicyRoots);
        var finalCodecBoundTypes = currentContractPolicyCodecs
            .Select(static codec => codec.TypeName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static type => type, StringComparer.Ordinal)
            .ToImmutableArray();

        var diagnostics = standalone.Diagnostics
            .Concat(contractPolicy.Diagnostics)
            .GroupBy(static item => (item.Kind, item.TypeName, item.Detail))
            .Select(static group => group.First())
            .ToImmutableArray();
        var codecOwnedEnumTypes = new HashSet<string>(
            currentContractPolicyCodecs
                .Where(static codec => codec.Kind is GeneratedCodecKind.Custom or GeneratedCodecKind.Adapter)
                .Select(static codec => codec.TypeName),
            StringComparer.Ordinal);
        var enums = standalone.Enums
            .Concat(contractDefault.Enums.Where(item => currentContractTypes.Contains(item.TypeName)))
            .Concat(contractPolicy.Enums.Where(item => currentContractTypes.Contains(item.TypeName)))
            .Where(item => !codecOwnedEnumTypes.Contains(item.TypeName))
            .GroupBy(static item => item.TypeName, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static item => item.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        return new DtoGenerationResult(
            globalCodecs,
            contractCodecs,
            finalCodecBoundTypes,
            diagnostics,
            enums)
        {
            CodecHashes = codecHashes
        };
    }

    private static ImmutableArray<GeneratedCodecModel> AttachCodecHashes(
        ImmutableArray<GeneratedCodecModel> codecs,
        ImmutableArray<GeneratedCodecHashModel> hashes)
    {
        var hashByType = hashes.ToDictionary(static item => item.TypeName, StringComparer.Ordinal);
        return codecs
            .Select(codec =>
            {
                if (!hashByType.TryGetValue(codec.TypeName, out var hash))
                {
                    throw new InvalidOperationException(
                        $"Final Codec graph is missing deterministic identity for generated Codec '{codec.TypeName}'.");
                }
                return codec with
                {
                    CodecHashHigh = hash.High,
                    CodecHashLow = hash.Low
                };
            })
            .ToImmutableArray();
    }

    private static bool ContainsRpcContract(INamespaceSymbol namespaceSymbol)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            if (ContainsRpcContract(type))
                return true;
        }
        foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            if (ContainsRpcContract(nestedNamespace))
                return true;
        }
        return false;
    }

    private static void ExpandReverseCodecDependencyClosure(
        ImmutableArray<GeneratedCodecModel> codecs,
        HashSet<string> scopedTypes)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var codec in codecs)
            {
                if (scopedTypes.Contains(codec.TypeName))
                    continue;
                if (GetCodecDependencies(codec).Any(scopedTypes.Contains))
                    changed |= scopedTypes.Add(codec.TypeName);
            }
        }
        while (changed);
    }

    private static ImmutableArray<GeneratedCodecModel> SelectOwnedContractCodecs(
        ImmutableArray<GeneratedCodecModel> contractDefault,
        ImmutableArray<GeneratedCodecModel> contractPolicy,
        IReadOnlyCollection<string> policyRoots)
    {
        var defaultByType = contractDefault.ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        var policyTypes = new HashSet<string>(
            contractPolicy.Select(static codec => codec.TypeName),
            StringComparer.Ordinal);
        var scopedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policyRoot in policyRoots)
        {
            if (policyTypes.Contains(policyRoot))
                scopedTypes.Add(policyRoot);
        }

        foreach (var codec in contractPolicy)
        {
            if (!defaultByType.TryGetValue(codec.TypeName, out var defaultCodec) ||
                !HasSameFinalCodecBinding(defaultCodec, codec))
            {
                scopedTypes.Add(codec.TypeName);
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var codec in contractPolicy)
            {
                if (scopedTypes.Contains(codec.TypeName))
                    continue;
                if (GetCodecDependencies(codec).Any(scopedTypes.Contains))
                    changed |= scopedTypes.Add(codec.TypeName);
            }
        }
        while (changed);

        return contractPolicy
            .Where(codec => scopedTypes.Contains(codec.TypeName))
            .Select(codec =>
            {
                if (defaultByType.TryGetValue(codec.TypeName, out var defaultCodec) &&
                    HasSameFinalCodecBinding(defaultCodec, codec))
                {
                    return codec with { CodecName = defaultCodec.CodecName };
                }

                return codec with
                {
                    CodecName = "__SharpLinkGeneratedContractPolicyCodec_" +
                                Hashing.GetIdentifierHash("contract-policy|" + codec.TypeName)
                };
            })
            .OrderBy(static codec => codec.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static bool HasSameFinalCodecBinding(GeneratedCodecModel left, GeneratedCodecModel right)
    {
        if (!string.Equals(left.TypeName, right.TypeName, StringComparison.Ordinal) ||
            left.Kind != right.Kind || left.IsReferenceType != right.IsReferenceType ||
            !string.Equals(left.ElementType, right.ElementType, StringComparison.Ordinal) ||
            !string.Equals(left.KeyType, right.KeyType, StringComparison.Ordinal) ||
            !string.Equals(left.ValueType, right.ValueType, StringComparison.Ordinal) ||
            !string.Equals(left.CustomCodecType, right.CustomCodecType, StringComparison.Ordinal) ||
            !string.Equals(left.AdapterType, right.AdapterType, StringComparison.Ordinal) ||
            !string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal) ||
            !left.ConstructorMembers.SequenceEqual(right.ConstructorMembers, StringComparer.Ordinal) ||
            !left.AssemblyDependencies.SequenceEqual(right.AssemblyDependencies, StringComparer.Ordinal) ||
            left.Members.Length != right.Members.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Members.Length; index++)
        {
            if (left.Members[index] with { Location = null } != right.Members[index] with { Location = null })
                return false;
        }
        return true;
    }
}
