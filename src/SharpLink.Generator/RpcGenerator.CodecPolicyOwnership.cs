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
        _ = standaloneState.AnalyzeWithFinalCodecBindings();
        var standaloneGraph = standaloneState.ResolveFinalCodecGraph(
            includeSerializable: true,
            includeContracts: false);
        var standalone = standaloneState.FinalizeResolvedCodecCandidates(standaloneGraph);
        var standaloneHashes = standaloneState.BuildFinalCodecHashes(standaloneGraph);
        var standaloneCodecs = AttachCodecHashes(standalone.Codecs, standaloneGraph, standaloneHashes);

        var contractDefaultState = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: true,
            applyCodecPolicy: true,
            selectorOnlyContractDefault: true);
        _ = contractDefaultState.AnalyzeWithFinalCodecBindings();
        var contractDefaultGraph = contractDefaultState.ResolveFinalCodecGraph(
            includeSerializable: false,
            includeContracts: true);
        var contractDefault = contractDefaultState.FinalizeResolvedCodecCandidates(contractDefaultGraph);
        var contractDefaultHashes = contractDefaultState.BuildFinalCodecHashes(contractDefaultGraph);
        var contractDefaultCodecs = AttachCodecHashes(
            contractDefault.Codecs,
            contractDefaultGraph,
            contractDefaultHashes);

        var contractPolicyState = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: true,
            applyCodecPolicy: true,
            selectorOnlyContractDefault: false);
        _ = contractPolicyState.AnalyzeWithFinalCodecBindings();
        var contractPolicyGraph = contractPolicyState.ResolveFinalCodecGraph(
            includeSerializable: false,
            includeContracts: true);
        var contractPolicy = contractPolicyState.FinalizeResolvedCodecCandidates(contractPolicyGraph);
        var codecHashes = contractPolicyState.BuildFinalCodecHashes(contractPolicyGraph);
        var referencedCodecHashes = standaloneHashes
            .Concat(codecHashes)
            .Where(static hash => hash.IsReferenced)
            .GroupBy(static hash => hash.TypeName, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static hash => hash.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();
        var unsafeBlitAutoLayoutDiagnostics =
            DtoAnalysisState.BuildUnsafeBlitAutoLayoutDiagnostics(contractPolicyGraph);
        var unsafeBlitRequirements = BuildUnsafeBlitRequirements(standaloneGraph, contractPolicyGraph);
        var contractPolicyCodecs = AttachCodecHashes(
            contractPolicy.Codecs,
            contractPolicyGraph,
            codecHashes);

        var currentContractTypes = new HashSet<string>(
            contractPolicyGraph.Plans.Keys,
            StringComparer.Ordinal);
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
            standaloneGraph.Plans.Keys,
            StringComparer.Ordinal);
        var defaultHashByType = contractDefaultHashes.ToDictionary(
            static hash => hash.TypeName,
            static hash => new RpcHashValue(hash.High, hash.Low),
            StringComparer.Ordinal);
        var policyHashByType = codecHashes.ToDictionary(
            static hash => hash.TypeName,
            static hash => new RpcHashValue(hash.High, hash.Low),
            StringComparer.Ordinal);
        var globalExcludedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policyRoot in contractOwnedPolicyRoots)
        {
            if (standaloneTypes.Contains(policyRoot) ||
                !contractPolicyGraph.Plans.TryGetValue(policyRoot, out var policyPlan) ||
                !RequiresGeneratedFactory(policyPlan))
            {
                continue;
            }

            if (!contractDefaultGraph.Plans.TryGetValue(policyRoot, out var defaultPlan) ||
                !HasSameResolvedFactoryBinding(defaultPlan, policyPlan, defaultHashByType, policyHashByType))
            {
                globalExcludedTypes.Add(policyRoot);
            }
        }
        ExpandReverseCodecDependencyClosure(contractDefaultGraph, globalExcludedTypes);
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
            contractDefaultGraph,
            contractPolicyGraph,
            defaultHashByType,
            policyHashByType,
            contractOwnedPolicyRoots);
        var finalCodecBoundTypes = contractPolicyGraph.Plans.Values
            .Where(RequiresGeneratedFactory)
            .Select(static plan => plan.TypeName)
            .OrderBy(static type => type, StringComparer.Ordinal)
            .ToImmutableArray();

        var diagnostics = standalone.Diagnostics
            .Concat(contractPolicy.Diagnostics)
            .GroupBy(static item => (item.Kind, item.TypeName, item.Detail))
            .Select(static group => group.First())
            .ToImmutableArray();
        var codecOwnedEnumTypes = new HashSet<string>(
            contractPolicyGraph.Plans.Values
                .Where(static plan => plan is FinalCustomCodecPlan or FinalAdapterCodecPlan)
                .Select(static plan => plan.TypeName),
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
            CodecHashes = codecHashes,
            ReferencedCodecHashes = referencedCodecHashes,
            UnsafeBlitRequirements = unsafeBlitRequirements,
            UnsafeBlitAutoLayoutDiagnostics = unsafeBlitAutoLayoutDiagnostics,
            AssemblyLogicalIdentity = compilation.Assembly.Identity.Name
        };
    }

    private static ImmutableArray<GeneratedCodecModel> AttachCodecHashes(
        ImmutableArray<GeneratedCodecModel> codecs,
        FinalCodecGraph graph,
        ImmutableArray<GeneratedCodecHashModel> hashes)
    {
        var codecByType = codecs.ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        var hashByType = hashes.ToDictionary(static item => item.TypeName, StringComparer.Ordinal);
        return graph.Plans.Values
            .Where(RequiresGeneratedFactory)
            .OrderBy(static plan => plan.TypeName, StringComparer.Ordinal)
            .Select(plan =>
            {
                if (!codecByType.TryGetValue(plan.TypeName, out var codec))
                {
                    throw new InvalidOperationException(
                        $"Final Codec plan '{plan.TypeName}' requires a generated factory but candidate analysis produced none.");
                }
                codec = ApplyResolvedEmissionPlan(plan, codec);
                if (!MatchesGeneratedFactoryPlan(plan, codec))
                {
                    throw new InvalidOperationException(
                        $"Final Codec plan '{plan.TypeName}' does not match generated factory candidate kind '{codec.Kind}'.");
                }
                if (!hashByType.TryGetValue(plan.TypeName, out var hash))
                {
                    throw new InvalidOperationException(
                        $"Final Codec graph is missing deterministic identity for generated Codec '{plan.TypeName}'.");
                }
                return codec with
                {
                    CodecHashHigh = hash.High,
                    CodecHashLow = hash.Low
                };
            })
            .ToImmutableArray();
    }

    private static GeneratedCodecModel ApplyResolvedEmissionPlan(
        FinalCodecPlan plan,
        GeneratedCodecModel codec)
    {
        if (plan is not FinalGeneratedDtoCodecPlan dto)
            return codec;

        var resolvedByField = dto.Members.ToDictionary(static member => member.FieldId);
        var changed = false;
        var members = codec.Members.Select(member =>
        {
            if (!resolvedByField.TryGetValue(member.FieldId, out var resolved) ||
                resolved.Kind == member.Kind)
            {
                return member;
            }

            changed = true;
            return resolved.WireStrategy == FinalDtoMemberWireStrategy.ChildCodec
                ? member with
                {
                    Kind = GeneratedMemberKind.Complex,
                    FixedTypeName = null,
                    FixedSize = 0,
                    EnumUnderlyingType = null
                }
                : member with { Kind = resolved.Kind };
        }).ToImmutableArray();

        if (!changed)
            return codec;

        var schema = new StringBuilder(codec.TypeName);
        foreach (var member in members)
        {
            schema.Append('|').Append(member.FieldId).Append(':').Append(member.TypeName)
                .Append(':').Append(member.Kind).Append(':').Append(member.Required);
            if (member.Nullable)
                schema.Append(":nullable");
        }
        return codec with
        {
            Members = members,
            SchemaId = GetResolvedSchemaId(codec.TypeName, schema.ToString())
        };
    }

    private static string GetResolvedSchemaId(string typeName, string schema)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in schema)
        {
            hash ^= character;
            hash *= prime;
        }
        return typeName + ":" + hash.ToString("X16", InvariantCulture);
    }

    private static bool RequiresGeneratedFactory(FinalCodecPlan plan)
        => plan is FinalGeneratedDtoCodecPlan or
            FinalUnionCodecPlan or
            FinalCustomCodecPlan or
            FinalAdapterCodecPlan or
            FinalCollectionCodecPlan { WireStrategy: FinalCollectionWireStrategy.ChildCodec };

    private static bool MatchesGeneratedFactoryPlan(FinalCodecPlan plan, GeneratedCodecModel codec)
        => plan switch
        {
            FinalGeneratedDtoCodecPlan => codec.Kind == GeneratedCodecKind.Dto,
            FinalUnionCodecPlan => codec.Kind == GeneratedCodecKind.Union,
            FinalCustomCodecPlan custom =>
                codec.Kind == GeneratedCodecKind.Custom &&
                string.Equals(codec.CustomCodecType, custom.CodecTypeName, StringComparison.Ordinal),
            FinalAdapterCodecPlan adapter =>
                codec.Kind == GeneratedCodecKind.Adapter &&
                string.Equals(codec.AdapterType, adapter.AdapterTypeName, StringComparison.Ordinal) &&
                string.Equals(codec.AdapterId, adapter.AdapterId, StringComparison.Ordinal),
            FinalCollectionCodecPlan collection =>
                collection.WireStrategy == FinalCollectionWireStrategy.ChildCodec &&
                codec.Kind == collection.CollectionKind,
            _ => false
        };

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
        FinalCodecGraph graph,
        HashSet<string> scopedTypes)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var plan in graph.Plans.Values)
            {
                if (!RequiresGeneratedFactory(plan) || scopedTypes.Contains(plan.TypeName))
                    continue;
                if (DtoAnalysisState.GetFinalCodecPlanDependencies(plan).Any(scopedTypes.Contains))
                    changed |= scopedTypes.Add(plan.TypeName);
            }
        }
        while (changed);
    }

    private static ImmutableArray<GeneratedCodecModel> SelectOwnedContractCodecs(
        ImmutableArray<GeneratedCodecModel> contractDefault,
        ImmutableArray<GeneratedCodecModel> contractPolicy,
        FinalCodecGraph defaultGraph,
        FinalCodecGraph policyGraph,
        IReadOnlyDictionary<string, RpcHashValue> defaultHashes,
        IReadOnlyDictionary<string, RpcHashValue> policyHashes,
        IReadOnlyCollection<string> policyRoots)
    {
        var defaultByType = contractDefault.ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        var policyByType = contractPolicy.ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        var policyFactoryTypes = new HashSet<string>(
            policyGraph.Plans.Values.Where(RequiresGeneratedFactory).Select(static plan => plan.TypeName),
            StringComparer.Ordinal);
        var scopedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policyRoot in policyRoots)
        {
            if (policyFactoryTypes.Contains(policyRoot))
                scopedTypes.Add(policyRoot);
        }

        foreach (var policyPlan in policyGraph.Plans.Values.Where(RequiresGeneratedFactory))
        {
            if (!defaultGraph.Plans.TryGetValue(policyPlan.TypeName, out var defaultPlan) ||
                !HasSameResolvedFactoryBinding(defaultPlan, policyPlan, defaultHashes, policyHashes))
            {
                scopedTypes.Add(policyPlan.TypeName);
            }
        }

        ExpandReverseCodecDependencyClosure(policyGraph, scopedTypes);

        return scopedTypes
            .OrderBy(static type => type, StringComparer.Ordinal)
            .Select(type =>
            {
                if (!policyByType.TryGetValue(type, out var codec))
                {
                    throw new InvalidOperationException(
                        $"Resolved contract-owned Codec plan '{type}' requires a generated factory but candidate analysis produced none.");
                }

                if (defaultByType.TryGetValue(type, out var defaultCodec) &&
                    defaultGraph.Plans.TryGetValue(type, out var defaultPlan) &&
                    policyGraph.Plans.TryGetValue(type, out var policyPlan) &&
                    HasSameResolvedFactoryBinding(defaultPlan, policyPlan, defaultHashes, policyHashes))
                {
                    return codec with { CodecName = defaultCodec.CodecName };
                }

                return codec with
                {
                    CodecName = "__SharpLinkGeneratedContractPolicyCodec_" +
                                Hashing.GetIdentifierHash("contract-policy|" + type)
                };
            })
            .ToImmutableArray();
    }

    private static bool HasSameResolvedFactoryBinding(
        FinalCodecPlan left,
        FinalCodecPlan right,
        IReadOnlyDictionary<string, RpcHashValue> leftHashes,
        IReadOnlyDictionary<string, RpcHashValue> rightHashes)
    {
        if (left.Kind != right.Kind ||
            !string.Equals(left.TypeName, right.TypeName, StringComparison.Ordinal) ||
            !leftHashes.TryGetValue(left.TypeName, out var leftHash) ||
            !rightHashes.TryGetValue(right.TypeName, out var rightHash) ||
            leftHash != rightHash)
        {
            return false;
        }

        return (left, right) switch
        {
            (FinalCustomCodecPlan leftCustom, FinalCustomCodecPlan rightCustom) =>
                string.Equals(leftCustom.CodecTypeName, rightCustom.CodecTypeName, StringComparison.Ordinal),
            (FinalAdapterCodecPlan leftAdapter, FinalAdapterCodecPlan rightAdapter) =>
                string.Equals(leftAdapter.AdapterTypeName, rightAdapter.AdapterTypeName, StringComparison.Ordinal) &&
                string.Equals(leftAdapter.AdapterId, rightAdapter.AdapterId, StringComparison.Ordinal),
            _ => true
        };
    }

    private sealed partial class DtoAnalysisState
    {
        private readonly bool _selectorOnlyContractDefaults = false;
        private readonly HashSet<string> _contractOwnedPolicyRoots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExplicitBindingCandidate> _canonicalAssemblyBindings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CustomCodecRegistration> _canonicalCustomCodecBindings = new(StringComparer.Ordinal);

        internal IReadOnlyCollection<string> ContractOwnedPolicyRoots => _contractOwnedPolicyRoots;

        public DtoAnalysisState(
            Compilation compilation,
            CancellationToken cancellationToken,
            bool contractMode,
            bool applyCodecPolicy,
            bool selectorOnlyContractDefault)
        {
            _compilation = compilation;
            _cancellationToken = cancellationToken;
            _contractMode = contractMode;
            _applyCodecPolicy = applyCodecPolicy;
            _selectorOnlyContractDefaults = selectorOnlyContractDefault;
            _allowedAssemblyNames = ResolveReferenceAssemblyNames(compilation);
            _allowedAssemblyNames.Add(compilation.Assembly.Identity.Name);
            CollectAdapterRegistrations();
            if (!selectorOnlyContractDefault)
            {
                CollectCanonicalAssemblyCustomCodecBindings();
                CollectCanonicalAssemblyBindings();
                AddCanonicalPolicyBindingAliases();
            }
            if (_contractMode && !selectorOnlyContractDefault)
                CollectAssemblyRoutes();
        }

        private static string GetCanonicalPolicyTargetIdentity(ITypeSymbol type)
            => GetTypeName(type);

        private static bool HasSameCanonicalPolicyTarget(ITypeSymbol left, ITypeSymbol right)
            => string.Equals(GetCanonicalPolicyTargetIdentity(left), GetCanonicalPolicyTargetIdentity(right), StringComparison.Ordinal);

        private void CollectCanonicalAssemblyCustomCodecBindings()
        {
            foreach (var attribute in _compilation.Assembly.GetAttributes()
                         .Where(static attribute => IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAttribute"))
                         .OrderBy(static attribute => attribute.ToString(), StringComparer.Ordinal))
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? Location.None;
                if (attribute.ConstructorArguments.Length != 2 ||
                    attribute.ConstructorArguments[0].Value is not ITypeSymbol target ||
                    attribute.ConstructorArguments[1].Value is not ITypeSymbol codec)
                {
                    Report(DtoDiagnosticKind.CustomCodecBindingInvalid, _compilation.Assembly,
                        "assembly-level RpcCodec requires targetType and codecType", location);
                    continue;
                }
                if (HasTypeParameter(target))
                {
                    Report(DtoDiagnosticKind.CustomCodecTargetInvalid, target, "custom Codec target must be a closed type", location);
                    continue;
                }
                target = NormalizeAdapterTarget(target);
                if (IsFrameworkWirePrimitive(target))
                {
                    Report(DtoDiagnosticKind.BuiltinCustomCodecOverride, target,
                        "SharpLink framework wire primitive types have fixed wire semantics and cannot be rebound; wrap the value in a user-defined payload type if a custom wire representation is required", location);
                    continue;
                }
                AddCanonicalCustomCodecBinding(target, codec, location);
            }
        }

        private void AddCanonicalCustomCodecBinding(ITypeSymbol target, ITypeSymbol codec, Location location)
        {
            var identity = GetCanonicalPolicyTargetIdentity(target);
            if (_canonicalCustomCodecBindings.TryGetValue(identity, out var existing) &&
                !SymbolEqualityComparer.Default.Equals(existing.CodecType, codec))
            {
                Report(DtoDiagnosticKind.CustomCodecSelectionConflict, target,
                    "the target is explicitly bound to multiple custom Codec implementations", location);
                return;
            }
            var registration = ValidateCustomCodecWithCanonicalTarget(codec, target, location);
            if (registration is null)
                return;
            _customCodecBindings[target] = registration;
            _canonicalCustomCodecBindings[identity] = registration;
            if (_contractMode)
                _contractOwnedPolicyRoots.Add(identity);
        }

        private CustomCodecRegistration? ValidateCustomCodecWithCanonicalTarget(ITypeSymbol codecType, ITypeSymbol targetType, Location location)
        {
            if (codecType is not INamedTypeSymbol named)
            {
                Report(DtoDiagnosticKind.CustomCodecTypeInvalid, codecType, "custom Codec must be a closed, public sealed type", location);
                return null;
            }
            if (HasTypeParameter(named) || !IsEffectivelyPublic(named) || !named.IsSealed ||
                !named.InstanceConstructors.Any(static constructor => constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 0))
            {
                Report(DtoDiagnosticKind.CustomCodecTypeInvalid, codecType,
                    "custom Codec must be a public sealed type with a public parameterless constructor", location);
                return null;
            }
            var implementsTargetCodec = named.AllInterfaces.Any(item =>
                item.Name == "IRpcCodec" && item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions" &&
                item is INamedTypeSymbol { IsGenericType: true } generic && generic.TypeArguments.Length == 1 &&
                HasSameCanonicalPolicyTarget(generic.TypeArguments[0], targetType));
            if (!implementsTargetCodec)
            {
                Report(DtoDiagnosticKind.CustomCodecTypeInvalid, codecType,
                    $"custom Codec must implement IRpcCodec<{GetTypeName(targetType)}>", location);
                return null;
            }
            if (!HasValidOpaqueSemanticIdentity(named))
            {
                Report(DtoDiagnosticKind.CustomCodecIdentityInvalid, codecType,
                    "custom Codec must declare a non-zero fixed semantic identity via [RpcCodecSemanticIdentity(high, low)]", location);
                return null;
            }
            return new CustomCodecRegistration(named, location);
        }

        private void CollectCanonicalAssemblyBindings()
        {
            foreach (var attribute in _compilation.Assembly.GetAttributes()
                         .Where(static attribute => IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAdapterAttribute")))
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? Location.None;
                if (attribute.ConstructorArguments.Length != 2 || attribute.ConstructorArguments[0].Value is not ITypeSymbol target ||
                    attribute.ConstructorArguments[1].Value is not INamedTypeSymbol adapter)
                {
                    Report(DtoDiagnosticKind.AdapterBindingInvalid, _compilation.Assembly,
                        "assembly-level RpcCodecAdapter requires targetType and adapterType", location);
                    continue;
                }
                if (HasTypeParameter(target))
                {
                    Report(DtoDiagnosticKind.AdapterTargetInvalid, target, "Adapter target must be a closed type", location);
                    continue;
                }
                target = NormalizeAdapterTarget(target);
                if (IsFrameworkWirePrimitive(target))
                {
                    Report(DtoDiagnosticKind.BuiltinAdapterOverride, target,
                        "SharpLink framework wire primitive types have fixed wire semantics and cannot be rebound; wrap the value in a user-defined payload type if a custom wire representation is required", location);
                    continue;
                }
                AddCanonicalAssemblyBinding(target, new ExplicitBindingCandidate(adapter, location));
            }
        }

        private void AddCanonicalAssemblyBinding(ITypeSymbol target, ExplicitBindingCandidate candidate)
        {
            var identity = GetCanonicalPolicyTargetIdentity(target);
            if (_canonicalAssemblyBindings.TryGetValue(identity, out var existing))
            {
                if (!SymbolEqualityComparer.Default.Equals(existing.ImplementationType, candidate.ImplementationType))
                {
                    Report(DtoDiagnosticKind.AdapterSelectionConflict, target,
                        "the target is explicitly bound to multiple different Codec Adapters", candidate.Location);
                    return;
                }
                _assemblyBindings[target] = existing;
                return;
            }
            _assemblyBindings[target] = candidate;
            _canonicalAssemblyBindings[identity] = candidate;
        }

        private void AddCanonicalPolicyBindingAliases()
        {
            if (_canonicalAssemblyBindings.Count == 0 && _canonicalCustomCodecBindings.Count == 0)
                return;
            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(_compilation.Assembly.GlobalNamespace, roots, includeSerializable: !_contractMode, includeContracts: _contractMode);
            var reachable = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var root in roots.Values)
                CollectFinalBindingTypes(root, reachable, seen, 0);
            foreach (var reachableType in reachable.Values)
            {
                var lookupType = NormalizeAdapterTarget(reachableType);
                var identity = GetCanonicalPolicyTargetIdentity(lookupType);
                if (!_assemblyBindings.ContainsKey(lookupType) && _canonicalAssemblyBindings.TryGetValue(identity, out var adapterBinding))
                    _assemblyBindings[lookupType] = adapterBinding;
                if (!_customCodecBindings.ContainsKey(lookupType) && _canonicalCustomCodecBindings.TryGetValue(identity, out var customBinding))
                    _customCodecBindings[lookupType] = customBinding;
            }
        }

        internal DtoAnalysisPassResult AnalyzeWithFinalCodecBindings()
        {
            _ = Analyze();
            RejectRuntimeSizedUnsafeBlitTypes();
            return SnapshotAnalysisResult();
        }

        internal DtoAnalysisPassResult FinalizeResolvedCodecCandidates(FinalCodecGraph graph)
        {
            NormalizeGeneratedModuleDependencies(graph);
            return SnapshotAnalysisResult();
        }

        private DtoAnalysisPassResult SnapshotAnalysisResult()
        {
            var finalizedCodecs = FilterFailedCodecClosure(
                _models.Values.OrderBy(static model => model.TypeName, StringComparer.Ordinal).ToImmutableArray());
            return new DtoAnalysisPassResult(
                finalizedCodecs,
                _diagnostics.ToImmutableArray(),
                _enums.Values.OrderBy(static item => item.TypeName, StringComparer.Ordinal).ToImmutableArray());
        }

        private void RejectRuntimeSizedUnsafeBlitTypes()
        {
            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(_compilation.Assembly.GlobalNamespace, roots, includeSerializable: !_contractMode, includeContracts: _contractMode);
            var reachable = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var root in roots.Values)
                CollectFinalBindingTypes(root, reachable, seen, 0);
            foreach (var type in reachable.Values)
            {
                var typeName = GetTypeName(type);
                if (HasCodecPolicyCandidate(type) ||
                    HasReferencedGeneratedCodecIdentityCandidate(type))
                {
                    // Referenced generated Codec metadata is only a candidate here.
                    // ResolveFinalCodecPlan owns its ABI/hash validation and final selection.
                    continue;
                }
                if (!type.IsUnmanagedType || !IsRuntimeSizedUnsafeBlitType(type))
                    continue;
                Report(DtoDiagnosticKind.Unsupported, type,
                    "runtime-sized intrinsic unmanaged types such as System.Numerics.Vector<T> cannot use UnsafeBlit; register an explicit typed Codec or Codec Adapter");
                _failed.Add(typeName);
            }
        }

        private bool IsRuntimeSizedUnsafeBlitType(ITypeSymbol type)
            => IsRuntimeSizedUnsafeBlitType(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

        private bool IsRuntimeSizedUnsafeBlitType(ITypeSymbol type, HashSet<ITypeSymbol> seen)
        {
            var vectorDefinition = _compilation.GetTypeByMetadataName("System.Numerics.Vector`1");
            if (vectorDefinition is not null && type is INamedTypeSymbol vector &&
                SymbolEqualityComparer.Default.Equals(vector.OriginalDefinition, vectorDefinition))
            {
                return true;
            }
            if (!type.IsUnmanagedType || type is not INamedTypeSymbol named || !seen.Add(type))
                return false;
            foreach (var field in named.GetMembers().OfType<IFieldSymbol>().Where(static field => !field.IsStatic && !field.IsConst))
            {
                if (IsRuntimeSizedUnsafeBlitType(field.Type, seen))
                    return true;
            }
            return false;
        }

        private void NormalizeGeneratedModuleDependencies(FinalCodecGraph graph)
        {
            if (_models.Count == 0)
                return;

            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(_compilation.Assembly.GlobalNamespace, roots, includeSerializable: !_contractMode, includeContracts: _contractMode);
            var symbolsByType = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var root in roots.Values)
                CollectFinalBindingTypes(root, symbolsByType, seen, 0);

            var localFactoryTypes = new HashSet<string>(
                graph.Plans.Values.Where(RequiresGeneratedFactory).Select(static plan => plan.TypeName),
                StringComparer.Ordinal);
            foreach (var plan in graph.Plans.Values.Where(RequiresGeneratedFactory))
            {
                if (!_models.TryGetValue(plan.TypeName, out var model))
                    continue;
                if (plan is FinalCustomCodecPlan or FinalAdapterCodecPlan)
                {
                    _models[plan.TypeName] = model with { AssemblyDependencies = ImmutableArray<string>.Empty };
                    continue;
                }
                if (plan is FinalUnionCodecPlan)
                {
                    // Union analysis already owns exact declared case assembly dependencies.
                    continue;
                }

                var dependencies = new HashSet<string>(StringComparer.Ordinal);
                foreach (var dependencyTypeName in GetFinalCodecPlanDependencies(plan))
                {
                    if (localFactoryTypes.Contains(dependencyTypeName) ||
                        !symbolsByType.TryGetValue(dependencyTypeName, out var dependencyType))
                    {
                        continue;
                    }
                    var assembly = dependencyType.ContainingAssembly;
                    if (assembly is not null &&
                        !SymbolEqualityComparer.Default.Equals(assembly, _compilation.Assembly) &&
                        HasGeneratedAssemblyManifest(assembly))
                    {
                        dependencies.Add(assembly.Identity.ToString());
                    }
                }
                _models[plan.TypeName] = model with
                {
                    AssemblyDependencies = dependencies.OrderBy(static identity => identity, StringComparer.Ordinal).ToImmutableArray()
                };
            }
        }

        private void CollectFinalBindingTypes(
            ITypeSymbol type,
            Dictionary<string, ITypeSymbol> reachable,
            HashSet<ITypeSymbol> seen,
            int depth)
        {
            if (depth > MaximumDepth || !seen.Add(type))
                return;
            var typeName = GetTypeName(type);
            reachable[typeName] = type;
            if (_models.TryGetValue(typeName, out var finalModel) &&
                finalModel.Kind is GeneratedCodecKind.Custom or GeneratedCodecKind.Adapter)
            {
                return;
            }
            if (type is IArrayTypeSymbol array)
            {
                CollectFinalBindingTypes(array.ElementType, reachable, seen, depth + 1);
                return;
            }
            if (TryGetCollection(type, out _, out var elementType, out var keyType, out var valueType))
            {
                if (elementType is not null) CollectFinalBindingTypes(elementType, reachable, seen, depth + 1);
                if (keyType is not null) CollectFinalBindingTypes(keyType, reachable, seen, depth + 1);
                if (valueType is not null) CollectFinalBindingTypes(valueType, reachable, seen, depth + 1);
                return;
            }
            if (type is not INamedTypeSymbol named || IsThirdPartyType(type))
                return;
            foreach (var member in GetSerializableMembers(named))
                CollectFinalBindingTypes(GetMemberType(member), reachable, seen, depth + 1);
        }
    }
}
