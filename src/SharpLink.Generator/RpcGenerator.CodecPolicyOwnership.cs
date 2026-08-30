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
        var standaloneHashes = standaloneState.BuildFinalCodecHashes(
            includeSerializable: true,
            includeContracts: false);
        var standaloneCodecs = AttachCodecHashes(standalone.Codecs, standaloneHashes);

        var contractDefaultState = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: true,
            applyCodecPolicy: true,
            selectorOnlyContractDefault: true);
        var contractDefault = contractDefaultState.AnalyzeWithFinalCodecBindings();
        var contractDefaultHashes = contractDefaultState.BuildFinalCodecHashes(
            includeSerializable: false,
            includeContracts: true);
        var contractDefaultCodecs = AttachCodecHashes(contractDefault.Codecs, contractDefaultHashes);

        var contractPolicyState = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: true,
            applyCodecPolicy: true,
            selectorOnlyContractDefault: false);
        var contractPolicy = contractPolicyState.AnalyzeWithFinalCodecBindings();
        var codecHashes = contractPolicyState.BuildFinalCodecHashes(
            includeSerializable: false,
            includeContracts: true);
        var contractPolicyCodecs = AttachCodecHashes(contractPolicy.Codecs, codecHashes);

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
            CodecHashes = codecHashes,
            AssemblyLogicalIdentity = compilation.Assembly.Identity.Name
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
            left.Kind != right.Kind ||
            left.IsReferenceType != right.IsReferenceType ||
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
            => string.Equals(
                GetCanonicalPolicyTargetIdentity(left),
                GetCanonicalPolicyTargetIdentity(right),
                StringComparison.Ordinal);

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
                    Report(DtoDiagnosticKind.CustomCodecTargetInvalid, target,
                        "custom Codec target must be a closed type", location);
                    continue;
                }

                target = NormalizeAdapterTarget(target);
                if (IsFrameworkWirePrimitive(target))
                {
                    Report(DtoDiagnosticKind.BuiltinCustomCodecOverride, target,
                        "SharpLink framework wire primitive types have fixed wire semantics and cannot be rebound; wrap the value in a user-defined payload type if a custom wire representation is required",
                        location);
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

        private CustomCodecRegistration? ValidateCustomCodecWithCanonicalTarget(
            ITypeSymbol codecType,
            ITypeSymbol targetType,
            Location location)
        {
            if (codecType is not INamedTypeSymbol named)
            {
                Report(DtoDiagnosticKind.CustomCodecTypeInvalid, codecType,
                    "custom Codec must be a closed, public sealed type", location);
                return null;
            }

            if (HasTypeParameter(named) ||
                !IsEffectivelyPublic(named) ||
                !named.IsSealed ||
                !named.InstanceConstructors.Any(static constructor =>
                    constructor.DeclaredAccessibility == Accessibility.Public &&
                    constructor.Parameters.Length == 0))
            {
                Report(DtoDiagnosticKind.CustomCodecTypeInvalid, codecType,
                    "custom Codec must be a public sealed type with a public parameterless constructor", location);
                return null;
            }

            var implementsTargetCodec = named.AllInterfaces.Any(item =>
                item.Name == "IRpcCodec" &&
                item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions" &&
                item is INamedTypeSymbol { IsGenericType: true } generic &&
                generic.TypeArguments.Length == 1 &&
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
                if (attribute.ConstructorArguments.Length != 2 ||
                    attribute.ConstructorArguments[0].Value is not ITypeSymbol target ||
                    attribute.ConstructorArguments[1].Value is not INamedTypeSymbol adapter)
                {
                    Report(DtoDiagnosticKind.AdapterBindingInvalid, _compilation.Assembly,
                        "assembly-level RpcCodecAdapter requires targetType and adapterType", location);
                    continue;
                }
                if (HasTypeParameter(target))
                {
                    Report(DtoDiagnosticKind.AdapterTargetInvalid, target,
                        "Adapter target must be a closed type", location);
                    continue;
                }

                target = NormalizeAdapterTarget(target);
                if (IsFrameworkWirePrimitive(target))
                {
                    Report(DtoDiagnosticKind.BuiltinAdapterOverride, target,
                        "SharpLink framework wire primitive types have fixed wire semantics and cannot be rebound; wrap the value in a user-defined payload type if a custom wire representation is required",
                        location);
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
                        "the target is explicitly bound to multiple different Codec Adapters",
                        candidate.Location);
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
            CollectCurrentAssemblyRoots(
                _compilation.Assembly.GlobalNamespace,
                roots,
                includeSerializable: !_contractMode,
                includeContracts: _contractMode);
            var reachable = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var root in roots.Values)
                CollectFinalBindingTypes(root, reachable, seen, 0);

            foreach (var reachableType in reachable.Values)
            {
                var lookupType = NormalizeAdapterTarget(reachableType);
                var identity = GetCanonicalPolicyTargetIdentity(lookupType);
                if (!_assemblyBindings.ContainsKey(lookupType) &&
                    _canonicalAssemblyBindings.TryGetValue(identity, out var adapterBinding))
                {
                    _assemblyBindings[lookupType] = adapterBinding;
                }
                if (!_customCodecBindings.ContainsKey(lookupType) &&
                    _canonicalCustomCodecBindings.TryGetValue(identity, out var customBinding))
                {
                    _customCodecBindings[lookupType] = customBinding;
                }
            }
        }

        internal DtoAnalysisPassResult AnalyzeWithFinalCodecBindings()
        {
            _ = Analyze();
            PromoteSelectedFixedMembersToCodecBindings();
            NormalizeGeneratedModuleDependencies();
            var finalizedCodecs = FilterFailedCodecClosure(
                _models.Values.OrderBy(static model => model.TypeName, StringComparer.Ordinal).ToImmutableArray());
            return new DtoAnalysisPassResult(
                finalizedCodecs,
                _diagnostics.ToImmutableArray(),
                _enums.Values.OrderBy(static item => item.TypeName, StringComparer.Ordinal).ToImmutableArray());
        }

        internal HashSet<string> GetCurrentContractReachableTypeNames()
        {
            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(
                _compilation.Assembly.GlobalNamespace,
                roots,
                includeSerializable: false,
                includeContracts: true);
            var reachable = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var root in roots.Values)
                CollectFinalBindingTypes(root, reachable, seen, 0);
            return new HashSet<string>(reachable.Keys, StringComparer.Ordinal);
        }

        private void PromoteSelectedFixedMembersToCodecBindings()
        {
            if (!_applyCodecPolicy || _models.Count == 0)
                return;

            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(
                _compilation.Assembly.GlobalNamespace,
                roots,
                includeSerializable: !_contractMode,
                includeContracts: _contractMode);

            var reachable = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var root in roots.Values)
                CollectFinalBindingTypes(root, reachable, seen, 0);

            var dtoModels = _models.Values
                .Where(static model => model.Kind == GeneratedCodecKind.Dto)
                .ToArray();
            foreach (var model in dtoModels)
            {
                if (!reachable.TryGetValue(model.TypeName, out var type) || type is not INamedTypeSymbol named)
                    continue;

                var memberSymbols = GetSerializableMembers(named)
                    .ToDictionary(static member => member.Name, StringComparer.Ordinal);
                var members = model.Members.ToArray();
                var changed = false;
                for (var index = 0; index < members.Length; index++)
                {
                    var member = members[index];
                    if (member.Kind is not (GeneratedMemberKind.Fixed or GeneratedMemberKind.NullableFixed or GeneratedMemberKind.String) ||
                        !memberSymbols.TryGetValue(member.Name, out var memberSymbol))
                    {
                        continue;
                    }

                    var memberType = GetMemberType(memberSymbol);
                    if (!HasSelectedMemberCodec(memberType))
                        continue;

                    Visit(memberType, [], 0);
                    members[index] = member with
                    {
                        Kind = GeneratedMemberKind.Complex,
                        FixedTypeName = null,
                        FixedSize = 0,
                        EnumUnderlyingType = null
                    };
                    changed = true;
                }

                if (!changed)
                    continue;

                var finalizedMembers = members.ToImmutableArray();
                var schema = new StringBuilder(model.TypeName);
                foreach (var member in finalizedMembers)
                {
                    schema.Append('|').Append(member.FieldId).Append(':').Append(member.TypeName)
                        .Append(':').Append(member.Kind).Append(':').Append(member.Required);
                    if (member.Nullable)
                        schema.Append(":nullable");
                }
                _models[model.TypeName] = model with
                {
                    Members = finalizedMembers,
                    SchemaId = GetSchemaId(model.TypeName, schema.ToString())
                };
            }
        }

        private bool HasSelectedCompositeCodecDependency(ITypeSymbol type)
        {
            if (!TryGetCollection(type, out _, out var elementType, out var keyType, out var valueType))
                return false;

            return (elementType is not null && HasSelectedMemberCodec(elementType)) ||
                   (keyType is not null && HasSelectedMemberCodec(keyType)) ||
                   (valueType is not null && HasSelectedMemberCodec(valueType));
        }

        private bool HasSelectedMemberCodec(ITypeSymbol memberType)
        {
            if (IsFrameworkWirePrimitive(memberType))
                return false;
            if (TrySelectCustomCodec(memberType, out var customCodec))
                return customCodec is not null;

            AdapterRegistration? selected = null;
            var hasSelection = _contractMode
                ? TrySelectContractCodecOverride(memberType, out selected)
                : TrySelectAdapter(memberType, out selected);
            return hasSelection && selected is not null;
        }

        private void NormalizeGeneratedModuleDependencies()
        {
            if (_models.Count == 0)
                return;

            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(
                _compilation.Assembly.GlobalNamespace,
                roots,
                includeSerializable: !_contractMode,
                includeContracts: _contractMode);
            var symbolsByType = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var root in roots.Values)
                CollectFinalBindingTypes(root, symbolsByType, seen, 0);

            var localFactoryTypes = new HashSet<string>(_models.Keys, StringComparer.Ordinal);
            foreach (var model in _models.Values.ToArray())
            {
                if (model.Kind is GeneratedCodecKind.Custom or GeneratedCodecKind.Adapter)
                {
                    _models[model.TypeName] = model with
                    {
                        AssemblyDependencies = ImmutableArray<string>.Empty
                    };
                    continue;
                }

                var dependencies = new HashSet<string>(StringComparer.Ordinal);
                foreach (var dependencyTypeName in GetCodecDependencies(model))
                {
                    if (localFactoryTypes.Contains(dependencyTypeName) ||
                        !symbolsByType.TryGetValue(dependencyTypeName, out var dependencyType) ||
                        IsBuiltin(dependencyType))
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

                _models[model.TypeName] = model with
                {
                    AssemblyDependencies = dependencies
                        .OrderBy(static identity => identity, StringComparer.Ordinal)
                        .ToImmutableArray()
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
                if (elementType is not null)
                    CollectFinalBindingTypes(elementType, reachable, seen, depth + 1);
                if (keyType is not null)
                    CollectFinalBindingTypes(keyType, reachable, seen, depth + 1);
                if (valueType is not null)
                    CollectFinalBindingTypes(valueType, reachable, seen, depth + 1);
                return;
            }
            if (type is not INamedTypeSymbol named || IsThirdPartyType(type))
                return;

            foreach (var member in GetSerializableMembers(named))
                CollectFinalBindingTypes(GetMemberType(member), reachable, seen, depth + 1);
        }
    }
}
