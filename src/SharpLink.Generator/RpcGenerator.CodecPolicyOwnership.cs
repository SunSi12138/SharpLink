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
        var contractDefaultState = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: true,
            applyCodecPolicy: true,
            selectorOnlyContractDefault: true);
        var contractDefault = contractDefaultState.AnalyzeWithFinalCodecBindings();
        var contractPolicyState = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: true,
            applyCodecPolicy: true,
            selectorOnlyContractDefault: false);
        var contractPolicy = contractPolicyState.AnalyzeWithFinalCodecBindings();

        var contractManifestCodecs = contractPolicyState.BuildContractManifestCodecs(contractPolicy.Codecs);
        var currentContractTypes = new HashSet<string>(
            contractManifestCodecs.Select(static codec => codec.TypeName),
            StringComparer.Ordinal);
        var currentContractDefaultCodecs = contractDefault.Codecs
            .Where(codec => currentContractTypes.Contains(codec.TypeName))
            .ToImmutableArray();
        var currentContractPolicyCodecs = contractPolicy.Codecs
            .Where(codec => currentContractTypes.Contains(codec.TypeName))
            .ToImmutableArray();

        var standaloneTypes = new HashSet<string>(
            standalone.Codecs.Select(static codec => codec.TypeName),
            StringComparer.Ordinal);
        var contractCustomTypes = new HashSet<string>(
            currentContractPolicyCodecs
                .Where(static codec => codec.Kind == GeneratedCodecKind.Custom)
                .Select(static codec => codec.TypeName),
            StringComparer.Ordinal);
        var globalExcludedTypes = new HashSet<string>(
            contractCustomTypes.Where(type => !standaloneTypes.Contains(type)),
            StringComparer.Ordinal);
        ExpandReverseCodecDependencyClosure(currentContractDefaultCodecs, globalExcludedTypes);
        var globalByType = currentContractDefaultCodecs
            .Where(codec => !globalExcludedTypes.Contains(codec.TypeName))
            .ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        foreach (var codec in standalone.Codecs)
            globalByType[codec.TypeName] = codec;
        var globalCodecs = globalByType.Values
            .OrderBy(static codec => codec.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        var contractOwnedPolicyRoots = new HashSet<string>(
            contractPolicyState.ContractOwnedPolicyRoots.Where(currentContractTypes.Contains),
            StringComparer.Ordinal);
        foreach (var codec in currentContractPolicyCodecs)
        {
            if (codec.Kind == GeneratedCodecKind.Custom)
                contractOwnedPolicyRoots.Add(codec.TypeName);
        }
        var contractCodecs = SelectOwnedContractCodecs(
            currentContractDefaultCodecs,
            currentContractPolicyCodecs,
            contractOwnedPolicyRoots);

        var standaloneDiagnostics = standalone.Diagnostics.Where(diagnostic =>
            !IsBuiltinBindingOverride(diagnostic) ||
            !currentContractTypes.Contains(diagnostic.TypeName));
        var contractDiagnostics = contractPolicy.Diagnostics.Where(diagnostic =>
            !IsBuiltinBindingOverride(diagnostic) ||
            !currentContractTypes.Contains(diagnostic.TypeName));
        var diagnostics = standaloneDiagnostics
            .Concat(contractDiagnostics)
            .Select(diagnostic => NormalizeExplicitBindingDiagnostic(compilation, diagnostic, cancellationToken))
            .GroupBy(static item => (item.Kind, item.TypeName, item.Detail))
            .Select(static group => group.First())
            .ToImmutableArray();
        var codecOwnedEnumTypes = new HashSet<string>(
            contractManifestCodecs
                .Where(static codec => codec.Kind is GeneratedCodecKind.Custom or GeneratedCodecKind.Adapter or GeneratedCodecKind.Direct)
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
            contractManifestCodecs,
            diagnostics,
            enums);
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

    private static bool IsBuiltinBindingOverride(DtoDiagnosticModel diagnostic)
        => diagnostic.Kind is DtoDiagnosticKind.BuiltinAdapterOverride or
            DtoDiagnosticKind.BuiltinCustomCodecOverride;

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
                !HasSameCodecDefinition(defaultCodec, codec))
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
                    HasSameCodecDefinition(defaultCodec, codec))
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

    private static DtoDiagnosticModel NormalizeExplicitBindingDiagnostic(
        Compilation compilation,
        DtoDiagnosticModel diagnostic,
        CancellationToken cancellationToken)
    {
        if (diagnostic.Kind != DtoDiagnosticKind.AdapterTypeInvalid ||
            !diagnostic.Detail.StartsWith("direct Codec must", StringComparison.Ordinal))
        {
            return diagnostic;
        }

        var metadataName = diagnostic.TypeName.StartsWith("global::", StringComparison.Ordinal)
            ? diagnostic.TypeName.Substring("global::".Length)
            : diagnostic.TypeName;
        var implementation = compilation.GetTypeByMetadataName(metadataName);
        if (implementation is null)
            return diagnostic;

        var implementsAdapter = implementation.AllInterfaces.Any(static item =>
            item.Name == "IRpcCodecAdapter" &&
            item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions");
        var implementsCodec = implementation.AllInterfaces.Any(static item =>
            item.Name == "IRpcCodec" &&
            item.Arity == 1 &&
            item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions");
        if (implementsAdapter || implementsCodec)
            return diagnostic;

        if (diagnostic.Location?.SourceTree is { } tree)
        {
            var source = tree.GetText(cancellationToken).ToString(diagnostic.Location.SourceSpan);
            if (source.Contains("WireFormatId", StringComparison.Ordinal))
                return diagnostic;
        }

        return diagnostic with
        {
            Kind = DtoDiagnosticKind.AdapterRegistrationInvalid,
            Detail = $"selected Adapter '{diagnostic.TypeName}' has no valid RpcCodecAdapterRegistration"
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
                CollectCanonicalAssemblyBindingsWithEnumSupport();
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
                if (IsNonOverridableBuiltin(target) &&
                    !(_contractMode && HasNativeCodecRoute(_compilation.Assembly)))
                {
                    Report(DtoDiagnosticKind.BuiltinCustomCodecOverride, target,
                        "built-in primitive Codecs cannot be rebound to a custom Codec", location);
                    if (!_contractMode)
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

            var codecIdentity = named.GetAttributes().FirstOrDefault(static attribute =>
                IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecImplementationAttribute"));
            if (codecIdentity is null ||
                codecIdentity.ConstructorArguments.Length != 2 ||
                codecIdentity.ConstructorArguments[0].Value is not string wireFormatId ||
                codecIdentity.ConstructorArguments[1].Value is not string schemaId ||
                !IsStableIdentity(wireFormatId) ||
                !IsStableIdentity(schemaId))
            {
                Report(DtoDiagnosticKind.CustomCodecIdentityInvalid, codecType,
                    "custom Codec must declare stable ASCII WireFormatId and SchemaId via [RpcCodecImplementation]", location);
                return null;
            }

            return new CustomCodecRegistration(named, wireFormatId, schemaId, location);
        }

        private void CollectCanonicalAssemblyBindingsWithEnumSupport()
        {
            foreach (var attribute in _compilation.Assembly.GetAttributes()
                         .Where(static attribute => IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAdapterAttribute")))
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? Location.None;
                if (attribute.ConstructorArguments.Length != 2 ||
                    attribute.ConstructorArguments[0].Value is not ITypeSymbol target ||
                    attribute.ConstructorArguments[1].Value is not INamedTypeSymbol implementation)
                {
                    Report(DtoDiagnosticKind.AdapterBindingInvalid, _compilation.Assembly,
                        "assembly-level RpcCodecAdapter requires targetType and an adapter or direct Codec implementation type", location);
                    continue;
                }
                if (HasTypeParameter(target))
                {
                    Report(DtoDiagnosticKind.AdapterTargetInvalid, target,
                        "Codec target must be a closed type", location);
                    continue;
                }

                target = NormalizeAdapterTarget(target);
                if (IsNonOverridableBuiltin(target) &&
                    !IsEnumOrNullableEnum(target) &&
                    !_contractMode)
                {
                    Report(DtoDiagnosticKind.BuiltinAdapterOverride, target,
                        "built-in primitive Codecs cannot be rebound by RpcCodecAdapter", location);
                    continue;
                }

                AddCanonicalAssemblyBinding(
                    target,
                    new ExplicitBindingCandidate(implementation, GetAttributeWireFormatId(attribute), location));
            }
        }

        private void AddCanonicalAssemblyBinding(ITypeSymbol target, ExplicitBindingCandidate candidate)
        {
            var identity = GetCanonicalPolicyTargetIdentity(target);
            if (_canonicalAssemblyBindings.TryGetValue(identity, out var existing))
            {
                if (!SymbolEqualityComparer.Default.Equals(existing.ImplementationType, candidate.ImplementationType) ||
                    !string.Equals(existing.WireFormatId, candidate.WireFormatId, StringComparison.Ordinal))
                {
                    Report(DtoDiagnosticKind.AdapterSelectionConflict, target,
                        "the target is explicitly bound to multiple different Codec implementations",
                        candidate.Location);
                    return;
                }

                _assemblyBindings[target] = existing;
                return;
            }

            var storedCandidate = PrepareCanonicalDirectBinding(target, candidate);
            _assemblyBindings[target] = storedCandidate;
            _canonicalAssemblyBindings[identity] = storedCandidate;
        }

        private ExplicitBindingCandidate PrepareCanonicalDirectBinding(
            ITypeSymbol target,
            ExplicitBindingCandidate candidate)
        {
            if (ImplementsRpcCodecAdapter(candidate.ImplementationType) ||
                candidate.WireFormatId is not { } wireFormatId ||
                !IsStableIdentity(wireFormatId) ||
                !IsCanonicalDirectCodecType(candidate.ImplementationType, target))
            {
                return candidate;
            }

            var registration = new AdapterRegistration(
                candidate.ImplementationType,
                AdapterId: null,
                WireFormatId: wireFormatId,
                SelectorType: null,
                Location: candidate.Location,
                IsDirectCodec: true);
            _adaptersByType[candidate.ImplementationType] = registration;
            return new ExplicitBindingCandidate(candidate.ImplementationType, WireFormatId: null, candidate.Location);
        }

        private static bool IsCanonicalDirectCodecType(INamedTypeSymbol type, ITypeSymbol target)
            => IsEffectivelyPublic(type) &&
               type.TypeKind == TypeKind.Class &&
               type.IsSealed &&
               !type.IsAbstract &&
               !HasTypeParameter(type) &&
               type.InstanceConstructors.Any(static constructor =>
                   constructor.DeclaredAccessibility == Accessibility.Public &&
                   constructor.Parameters.Length == 0) &&
               type.AllInterfaces.Any(item =>
                   item.Name == "IRpcCodec" &&
                   item.Arity == 1 &&
                   item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions" &&
                   HasSameCanonicalPolicyTarget(item.TypeArguments[0], target));

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
            return new DtoAnalysisPassResult(
                _models.Values.OrderBy(static model => model.TypeName, StringComparer.Ordinal).ToImmutableArray(),
                _diagnostics.ToImmutableArray(),
                _enums.Values.OrderBy(static item => item.TypeName, StringComparer.Ordinal).ToImmutableArray());
        }

        internal ImmutableArray<GeneratedCodecModel> BuildContractManifestCodecs(
            ImmutableArray<GeneratedCodecModel> selectedCodecs)
        {
            var selectedByType = selectedCodecs.ToDictionary(
                static codec => codec.TypeName, StringComparer.Ordinal);
            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(
                _compilation.Assembly.GlobalNamespace,
                roots,
                includeSerializable: false,
                includeContracts: true);

            var symbolsByType = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var root in roots.Values)
                CollectFinalBindingTypes(root, symbolsByType, seen, 0);

            var result = new Dictionary<string, GeneratedCodecModel>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            foreach (var root in roots.Values)
            {
                var rootName = GetTypeName(root);
                symbolsByType[rootName] = root;
                pending.Enqueue(rootName);
            }

            while (pending.Count != 0)
            {
                var typeName = pending.Dequeue();
                if (result.ContainsKey(typeName))
                    continue;

                if (selectedByType.TryGetValue(typeName, out var selected))
                {
                    result.Add(typeName, selected);
                    foreach (var dependency in GetCodecDependencies(selected))
                        pending.Enqueue(dependency);
                    continue;
                }

                if (!symbolsByType.TryGetValue(typeName, out var type) ||
                    !TryCreateImplicitContractManifestCodec(type, out var implicitCodec))
                {
                    continue;
                }

                result.Add(typeName, implicitCodec);
                if (implicitCodec.Kind == GeneratedCodecKind.Native &&
                    TryGetCollection(type, out _, out var elementType, out var keyType, out var valueType))
                {
                    if (elementType is not null)
                        pending.Enqueue(GetTypeName(elementType));
                    if (keyType is not null)
                        pending.Enqueue(GetTypeName(keyType));
                    if (valueType is not null)
                        pending.Enqueue(GetTypeName(valueType));
                }
            }

            return result.Values
                .OrderBy(static codec => codec.TypeName, StringComparer.Ordinal)
                .ToImmutableArray();
        }

        private bool TryCreateImplicitContractManifestCodec(
            ITypeSymbol type,
            out GeneratedCodecModel codec)
        {
            if (!IsBuiltin(type))
            {
                codec = null!;
                return false;
            }

            var typeName = GetTypeName(type);
            var kind = IsImplicitUnsafeBlitNullable(type)
                ? GeneratedCodecKind.UnsafeBlit
                : IsNativeCodecType(type)
                    ? GeneratedCodecKind.Native
                    : GeneratedCodecKind.UnsafeBlit;
            var schemaIdentity = kind == GeneratedCodecKind.Native
                ? "implicit-native"
                : GetUnsafeBlitSchemaIdentity(type);
            if (kind == GeneratedCodecKind.Native &&
                type.TypeKind == TypeKind.Enum &&
                type is INamedTypeSymbol { EnumUnderlyingType: { } underlying })
            {
                schemaIdentity += "|enum:" + GetTypeName(underlying);
            }

            codec = new GeneratedCodecModel(
                typeName,
                CodecName: string.Empty,
                GetSchemaId(typeName, schemaIdentity),
                kind,
                type.IsReferenceType,
                ImmutableArray<GeneratedMemberModel>.Empty,
                ImmutableArray<string>.Empty,
                ElementType: null,
                KeyType: null,
                ValueType: null,
                CustomCodecType: null,
                AdapterType: null,
                AdapterId: null,
                "sharplink-native/v1",
                GetAssemblyDependencies([type]),
                type.Locations.FirstOrDefault());
            return true;
        }

        private static bool IsImplicitUnsafeBlitNullable(ITypeSymbol type)
            => type.IsUnmanagedType &&
               type is INamedTypeSymbol nullable &&
               nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
               !IsNonOverridableBuiltin(type);

        private string GetUnsafeBlitSchemaIdentity(ITypeSymbol type)
        {
            var builder = new StringBuilder("implicit-unsafe-blit");
            AppendUnsafeBlitLayout(type, builder, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), 0);
            return builder.ToString();
        }

        private void AppendUnsafeBlitLayout(
            ITypeSymbol type,
            StringBuilder builder,
            HashSet<ITypeSymbol> stack,
            int depth)
        {
            builder.Append('|').Append(GetTypeName(type));
            if (type.TypeKind == TypeKind.Enum &&
                type is INamedTypeSymbol { EnumUnderlyingType: { } enumUnderlying })
            {
                builder.Append("|enum:").Append(GetTypeName(enumUnderlying));
                return;
            }

            if (type.SpecialType != SpecialType.None ||
                type is IPointerTypeSymbol or IFunctionPointerTypeSymbol ||
                type is not INamedTypeSymbol named)
            {
                return;
            }

            AppendReferencedMetadataIdentity(named, builder);
            AppendWireLayoutAttribute(builder, named, "System.Runtime.InteropServices.StructLayoutAttribute");
            AppendWireLayoutAttribute(builder, named, "System.Runtime.CompilerServices.InlineArrayAttribute");

            if (!stack.Add(type))
            {
                builder.Append("|recursive");
                return;
            }

            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                named.TypeArguments.Length == 1)
            {
                builder.Append("|nullable-underlying");
                AppendUnsafeBlitLayout(named.TypeArguments[0], builder, stack, depth + 1);
                stack.Remove(type);
                return;
            }

            var fields = named.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(static field => !field.IsStatic && !field.IsConst)
                .ToArray();
            builder.Append("|fields:").Append(fields.Length.ToString(InvariantCulture));
            for (var index = 0; index < fields.Length; index++)
            {
                var field = fields[index];
                builder.Append("|field:")
                    .Append(index.ToString(InvariantCulture))
                    .Append(':')
                    .Append(field.Name);
                if (field.IsFixedSizeBuffer)
                {
                    builder.Append("|fixed-buffer:")
                        .Append(field.FixedSize.ToString(InvariantCulture));
                }
                AppendWireLayoutAttribute(builder, field, "System.Runtime.InteropServices.FieldOffsetAttribute");
                AppendWireLayoutAttribute(builder, field, "System.Runtime.CompilerServices.FixedBufferAttribute");
                AppendUnsafeBlitLayout(field.Type, builder, stack, depth + 1);
            }

            stack.Remove(type);
        }

        private void AppendReferencedMetadataIdentity(INamedTypeSymbol type, StringBuilder builder)
        {
            var owner = type.ContainingAssembly;
            if (owner is null || SymbolEqualityComparer.Default.Equals(owner, _compilation.Assembly))
                return;

            foreach (var reference in _compilation.References)
            {
                var symbol = _compilation.GetAssemblyOrModuleSymbol(reference);
                var referencedAssembly = symbol switch
                {
                    IAssemblySymbol assembly => assembly,
                    IModuleSymbol module => module.ContainingAssembly,
                    _ => null
                };
                if (referencedAssembly is null ||
                    !SymbolEqualityComparer.Default.Equals(referencedAssembly, owner) ||
                    reference is not PortableExecutableReference portable)
                {
                    continue;
                }

                builder.Append("|metadata-owner:").Append(owner.Identity).Append('|');
                var metadata = portable.GetMetadata();
                if (metadata is AssemblyMetadata assemblyMetadata)
                {
                    foreach (var module in assemblyMetadata.GetModules())
                        builder.Append(module.GetModuleVersionId().ToString("D")).Append(';');
                }
                else if (metadata is ModuleMetadata moduleMetadata)
                {
                    builder.Append(moduleMetadata.GetModuleVersionId().ToString("D"));
                }
                return;
            }

            builder.Append("|metadata-owner-unresolved:").Append(owner.Identity);
        }

        private static void AppendWireLayoutAttribute(
            StringBuilder builder,
            ISymbol symbol,
            string attributeName)
        {
            var attribute = symbol.GetAttributes().FirstOrDefault(item =>
                string.Equals(item.AttributeClass?.ToDisplayString(), attributeName, StringComparison.Ordinal));
            if (attribute is null)
                return;

            builder.Append("|attr:").Append(attributeName);
            foreach (var argument in attribute.ConstructorArguments)
                AppendWireLayoutConstant(builder, argument);
            foreach (var argument in attribute.NamedArguments.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                builder.Append('|').Append(argument.Key).Append('=');
                AppendWireLayoutConstant(builder, argument.Value);
            }
        }

        private static void AppendWireLayoutConstant(StringBuilder builder, TypedConstant constant)
        {
            builder.Append(':').Append(constant.Type is null ? "?" : GetTypeName(constant.Type)).Append('=');
            if (constant.Kind == TypedConstantKind.Array)
            {
                builder.Append('[');
                foreach (var item in constant.Values)
                    AppendWireLayoutConstant(builder, item);
                builder.Append(']');
                return;
            }

            if (constant.Value is ITypeSymbol type)
            {
                builder.Append(GetTypeName(type));
                return;
            }

            builder.Append(Convert.ToString(constant.Value, InvariantCulture) ?? "null");
        }

        private void CollectContractBuiltinCustomCodecBindings()
        {
            foreach (var attribute in _compilation.Assembly.GetAttributes()
                         .Where(static attribute => IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAttribute"))
                         .OrderBy(static attribute => attribute.ToString(), StringComparer.Ordinal))
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? Location.None;
                if (attribute.ConstructorArguments.Length != 2 ||
                    attribute.ConstructorArguments[0].Value is not ITypeSymbol target ||
                    attribute.ConstructorArguments[1].Value is not ITypeSymbol codec ||
                    HasTypeParameter(target))
                {
                    continue;
                }

                target = NormalizeAdapterTarget(target);
                if (!IsNonOverridableBuiltin(target))
                    continue;
                AddCustomCodecBinding(target, codec, location);
            }
        }

        private void CollectAssemblyBindingsWithEnumSupport()
        {
            foreach (var attribute in _compilation.Assembly.GetAttributes()
                         .Where(static attribute => IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAdapterAttribute")))
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? Location.None;
                if (attribute.ConstructorArguments.Length != 2 ||
                    attribute.ConstructorArguments[0].Value is not ITypeSymbol target ||
                    attribute.ConstructorArguments[1].Value is not INamedTypeSymbol implementation)
                {
                    Report(DtoDiagnosticKind.AdapterBindingInvalid, _compilation.Assembly,
                        "assembly-level RpcCodecAdapter requires targetType and an adapter or direct Codec implementation type", location);
                    continue;
                }
                if (HasTypeParameter(target))
                {
                    Report(DtoDiagnosticKind.AdapterTargetInvalid, target,
                        "Codec target must be a closed type", location);
                    continue;
                }
                target = NormalizeAdapterTarget(target);
                if (IsNonOverridableBuiltin(target) &&
                    !IsEnumOrNullableEnum(target) &&
                    !_contractMode)
                {
                    Report(DtoDiagnosticKind.BuiltinAdapterOverride, target,
                        "built-in primitive Codecs cannot be rebound by RpcCodecAdapter", location);
                    continue;
                }
                AddAssemblyBinding(
                    target,
                    new ExplicitBindingCandidate(implementation, GetAttributeWireFormatId(attribute), location));
            }
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
                if (model.Kind is GeneratedCodecKind.Custom or GeneratedCodecKind.Adapter or GeneratedCodecKind.Direct)
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
            reachable[GetTypeName(type)] = type;

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
