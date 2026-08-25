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

        var contractPolicies = ImmutableArray.CreateBuilder<GeneratedContractCodecPolicy>();
        var policyDiagnostics = ImmutableArray.CreateBuilder<DtoDiagnosticModel>();
        var policyEnums = ImmutableArray.CreateBuilder<GeneratedEnumModel>();
        foreach (var contract in CollectCurrentRpcContracts(compilation.Assembly.GlobalNamespace)
                     .OrderBy(static item => GetTypeName(item), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contractTypeName = GetTypeName(contract);
            var perContractDefaultState = new DtoAnalysisState(
                compilation,
                cancellationToken,
                contractMode: true,
                applyCodecPolicy: true,
                selectorOnlyContractDefault: true,
                contractRoot: contract);
            var perContractDefault = perContractDefaultState.AnalyzeWithFinalCodecBindings();
            var perContractPolicyState = new DtoAnalysisState(
                compilation,
                cancellationToken,
                contractMode: true,
                applyCodecPolicy: true,
                selectorOnlyContractDefault: false,
                contractRoot: contract);
            var perContractPolicy = perContractPolicyState.AnalyzeWithFinalCodecBindings();
            var ownedCodecs = SelectOwnedContractCodecs(
                perContractDefault.Codecs,
                perContractPolicy.Codecs,
                perContractPolicyState.ContractOwnedPolicyRoots,
                contractTypeName);
            var dependencies = perContractPolicy.Codecs
                .SelectMany(static codec => codec.AssemblyDependencies)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static dependency => dependency, StringComparer.Ordinal)
                .ToImmutableArray();
            contractPolicies.Add(new GeneratedContractCodecPolicy(
                contractTypeName,
                perContractPolicy.Codecs,
                ownedCodecs,
                !ownedCodecs.IsDefaultOrEmpty,
                dependencies));
            policyDiagnostics.AddRange(perContractPolicy.Diagnostics);
            policyEnums.AddRange(perContractPolicy.Enums);
        }

        var globalByType = contractDefault.Codecs.ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        foreach (var codec in standalone.Codecs)
            globalByType[codec.TypeName] = codec;
        var globalCodecs = globalByType.Values
            .OrderBy(static codec => codec.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        var contractCodecs = contractPolicies
            .SelectMany(static policy => policy.OwnedCodecs)
            .GroupBy(static codec => codec.CodecName, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static codec => codec.CodecName, StringComparer.Ordinal)
            .ToImmutableArray();

        var contractManifestCodecs = contractPolicies
            .SelectMany(static policy => policy.Codecs)
            .GroupBy(static codec => codec.TypeName, StringComparer.Ordinal)
            .Select(static group => group.OrderBy(static codec => codec.SchemaId, StringComparer.Ordinal).First())
            .OrderBy(static codec => codec.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        var diagnostics = standalone.Diagnostics
            .Concat(policyDiagnostics)
            .Select(diagnostic => NormalizeExplicitBindingDiagnostic(compilation, diagnostic, cancellationToken))
            .GroupBy(static item => (item.Kind, item.TypeName, item.Detail))
            .Select(static group => group.First())
            .ToImmutableArray();
        var enums = standalone.Enums
            .Concat(contractDefault.Enums)
            .Concat(policyEnums)
            .GroupBy(static item => item.TypeName, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static item => item.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        return new DtoGenerationResult(
            globalCodecs,
            contractCodecs,
            contractManifestCodecs,
            diagnostics,
            enums,
            contractPolicies.ToImmutable());
    }

    private static IEnumerable<INamedTypeSymbol> CollectCurrentRpcContracts(INamespaceSymbol namespaceSymbol)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            foreach (var contract in CollectCurrentRpcContracts(type))
                yield return contract;
        }
        foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var contract in CollectCurrentRpcContracts(nestedNamespace))
                yield return contract;
        }
    }

    private static IEnumerable<INamedTypeSymbol> CollectCurrentRpcContracts(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Interface && HasRpcContractAttribute(type))
            yield return type;
        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var contract in CollectCurrentRpcContracts(nested))
                yield return contract;
        }
    }

    private static ImmutableArray<GeneratedCodecModel> SelectOwnedContractCodecs(
        ImmutableArray<GeneratedCodecModel> contractDefault,
        ImmutableArray<GeneratedCodecModel> contractPolicy,
        IReadOnlyCollection<string> policyRoots,
        string policyOwnerKey)
    {
        var defaultByType = contractDefault.ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        var policyTypes = new HashSet<string>(contractPolicy.Select(static codec => codec.TypeName), StringComparer.Ordinal);
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
                scopedTypes.Add(codec.TypeName);
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
                    return codec with { CodecName = defaultCodec.CodecName };

                return codec with
                {
                    CodecName = "__SharpLinkGeneratedContractPolicyCodec_" +
                                Hashing.GetIdentifierHash("contract-policy|" + policyOwnerKey + "|" + codec.TypeName)
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
            return diagnostic;

        var metadataName = diagnostic.TypeName.StartsWith("global::", StringComparison.Ordinal)
            ? diagnostic.TypeName.Substring("global::".Length)
            : diagnostic.TypeName;
        var implementation = compilation.GetTypeByMetadataName(metadataName);
        if (implementation is null)
            return diagnostic;

        var implementsAdapter = implementation.AllInterfaces.Any(static item =>
            item.Name == "IRpcCodecAdapter" && item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions");
        var implementsCodec = implementation.AllInterfaces.Any(static item =>
            item.Name == "IRpcCodec" && item.Arity == 1 && item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions");
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
        private readonly INamedTypeSymbol? _contractRoot;

        internal IReadOnlyCollection<string> ContractOwnedPolicyRoots => _contractOwnedPolicyRoots;

        public DtoAnalysisState(
            Compilation compilation,
            CancellationToken cancellationToken,
            bool contractMode,
            bool applyCodecPolicy,
            bool selectorOnlyContractDefault)
            : this(compilation, cancellationToken, contractMode, applyCodecPolicy, selectorOnlyContractDefault, contractRoot: null)
        {
        }

        public DtoAnalysisState(
            Compilation compilation,
            CancellationToken cancellationToken,
            bool contractMode,
            bool applyCodecPolicy,
            bool selectorOnlyContractDefault,
            INamedTypeSymbol? contractRoot)
        {
            _compilation = compilation;
            _cancellationToken = cancellationToken;
            _contractMode = contractMode;
            _applyCodecPolicy = applyCodecPolicy;
            _selectorOnlyContractDefaults = selectorOnlyContractDefault;
            _contractRoot = contractRoot;
            _allowedAssemblyNames = ResolveReferenceAssemblyNames(compilation);
            _allowedAssemblyNames.Add(compilation.Assembly.Identity.Name);
            CollectAdapterRegistrations();
            if (!selectorOnlyContractDefault)
                CollectAssemblyBindingsWithEnumSupport();
            if (_contractMode && !selectorOnlyContractDefault)
                CollectAssemblyRoutes();
        }

        internal DtoAnalysisPassResult AnalyzeWithFinalCodecBindings()
        {
            if (_contractMode && _contractRoot is not null)
            {
                var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
                CollectContractPayloadRoots(_contractRoot, roots);
                foreach (var root in roots.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    Visit(root.Value, [], 0);
                }
            }
            else
            {
                _ = Analyze();
            }
            // Final route selection must run before the default Nullable<Enum> wrapper is materialized.
            // Otherwise the wrapper occupies the Type key and prevents a matching Native route from
            // replacing it with the Contract-owned Adapter/Direct selection.
            PromoteSelectedFixedMembersToCodecBindings();
            EnsureNativeNullableEnumModels();
            return new DtoAnalysisPassResult(
                _models.Values.OrderBy(static model => model.TypeName, StringComparer.Ordinal).ToImmutableArray(),
                _diagnostics.ToImmutableArray(),
                _enums.Values.OrderBy(static item => item.TypeName, StringComparer.Ordinal).ToImmutableArray());
        }

        private void EnsureNativeNullableEnumModels()
        {
            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            if (_contractMode && _contractRoot is not null)
            {
                CollectContractPayloadRoots(_contractRoot, roots);
            }
            else
            {
                CollectCurrentAssemblyRoots(
                    _compilation.Assembly.GlobalNamespace,
                    roots,
                    includeSerializable: !_contractMode,
                    includeContracts: _contractMode);
                if (_contractMode)
                    CollectReferencedContractRoots(roots);
            }

            var reachable = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var root in roots.Values)
                CollectFinalBindingTypes(root, reachable, seen, 0);

            foreach (var type in reachable.Values)
            {
                if (type is not INamedTypeSymbol nullable ||
                    nullable.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T ||
                    nullable.TypeArguments.Length != 1 ||
                    nullable.TypeArguments[0].TypeKind != TypeKind.Enum)
                {
                    continue;
                }

                var typeName = GetTypeName(type);
                if (_models.ContainsKey(typeName) || _failed.Contains(typeName))
                    continue;

                var enumType = nullable.TypeArguments[0];
                Visit(enumType, [], 0);
                if (_failed.Contains(GetTypeName(enumType)))
                {
                    _failed.Add(typeName);
                    continue;
                }

                _models[typeName] = new GeneratedCodecModel(
                    typeName,
                    GetCodecName(typeName, _contractMode),
                    GetSchemaId(typeName, GeneratedCodecKind.Nullable.ToString()),
                    GeneratedCodecKind.Nullable,
                    IsReferenceType: false,
                    ImmutableArray<GeneratedMemberModel>.Empty,
                    ImmutableArray<string>.Empty,
                    GetTypeName(enumType),
                    KeyType: null,
                    ValueType: null,
                    AdapterType: null,
                    AdapterId: null,
                    "sharplink-native/v1",
                    GetAssemblyDependencies([type]),
                    type.Locations.FirstOrDefault());
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
                    Report(DtoDiagnosticKind.AdapterTargetInvalid, target, "Codec target must be a closed type", location);
                    continue;
                }
                target = NormalizeAdapterTarget(target);
                if (IsNonOverridableBuiltin(target) &&
                    !IsEnumOrNullableEnum(target) &&
                    (!_contractMode || !HasDeclaredRouteForScope(target)))
                {
                    Report(DtoDiagnosticKind.BuiltinAdapterOverride, target,
                        "built-in primitive Codecs cannot be rebound by RpcCodecAdapter unless a matching Contract route is also present",
                        location);
                    continue;
                }
                AddAssemblyBinding(target,
                    new ExplicitBindingCandidate(implementation, GetAttributeWireFormatId(attribute), location));
            }
        }

        private void PromoteSelectedFixedMembersToCodecBindings()
        {
            if (!_applyCodecPolicy || _models.Count == 0)
                return;

            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            if (_contractMode && _contractRoot is not null)
            {
                CollectContractPayloadRoots(_contractRoot, roots);
            }
            else
            {
                CollectCurrentAssemblyRoots(
                    _compilation.Assembly.GlobalNamespace,
                    roots,
                    includeSerializable: !_contractMode,
                    includeContracts: _contractMode);
                if (_contractMode)
                    CollectReferencedContractRoots(roots);
            }

            var reachable = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var root in roots.Values)
                CollectFinalBindingTypes(root, reachable, seen, 0);

            var dtoModels = _models.Values.Where(static model => model.Kind == GeneratedCodecKind.Dto).ToArray();
            foreach (var model in dtoModels)
            {
                if (!reachable.TryGetValue(model.TypeName, out var type) || type is not INamedTypeSymbol named)
                    continue;

                var memberSymbols = GetSerializableMembers(named).ToDictionary(static member => member.Name, StringComparer.Ordinal);
                var members = model.Members.ToArray();
                var changed = false;
                for (var index = 0; index < members.Length; index++)
                {
                    var member = members[index];
                    if (member.Kind is not (GeneratedMemberKind.Fixed or GeneratedMemberKind.NullableFixed) ||
                        !memberSymbols.TryGetValue(member.Name, out var memberSymbol))
                        continue;

                    var memberType = GetMemberType(memberSymbol);
                    AdapterRegistration? selected = null;
                    var hasSelection = _contractMode
                        ? TrySelectContractCodecOverride(memberType, out selected)
                        : TrySelectAdapter(memberType, out selected);
                    if (!hasSelection || selected is null)
                        continue;

                    Visit(memberType, [], 0);
                    members[index] = member with
                    {
                        Kind = GeneratedMemberKind.Complex,
                        FixedTypeName = null,
                        FixedSize = 0
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
