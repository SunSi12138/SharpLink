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

        // The global/default registry contains the normal generated graph. Contract-default models
        // preserve registered selector-attribute Adapter choices while omitting explicit
        // RpcCodecAdapter bindings and assembly routes. Contract-only custom [RpcCodec] selections
        // are also excluded here because their explicit provenance belongs to the Contract assembly
        // policy graph. Standalone [RpcSerializable] analysis still owns its historical explicit
        // Adapter/direct/custom semantics, including definitions shared transitively with an RPC
        // payload, so standalone models win matching TypeNames below.
        var standaloneTypes = new HashSet<string>(
            standalone.Codecs.Select(static codec => codec.TypeName),
            StringComparer.Ordinal);
        var globalByType = contractDefault.Codecs
            .Where(codec => codec.Kind != GeneratedCodecKind.Custom || standaloneTypes.Contains(codec.TypeName))
            .ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        foreach (var codec in standalone.Codecs)
            globalByType[codec.TypeName] = codec;
        var globalCodecs = globalByType.Values
            .OrderBy(static codec => codec.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        // Contract ownership is defined by explicit/route/custom selection provenance as well as the
        // resulting definition delta. Provenance matters when an explicit binding intentionally
        // selects the same definition that the default analysis would choose: it is still published
        // Contract policy and must not become runtime-overridable merely because the definitions
        // happen to compare equal. Native parents/dependencies are then pulled into the owner graph
        // so the changed policy remains closed over its full generated dependency graph.
        var contractOwnedPolicyRoots = new HashSet<string>(
            contractPolicyState.ContractOwnedPolicyRoots,
            StringComparer.Ordinal);
        foreach (var codec in contractPolicy.Codecs)
        {
            if (codec.Kind == GeneratedCodecKind.Custom)
                contractOwnedPolicyRoots.Add(codec.TypeName);
        }
        var contractCodecs = SelectOwnedContractCodecs(
            contractDefault.Codecs,
            contractPolicy.Codecs,
            contractOwnedPolicyRoots);
        var contractManifestCodecs = contractPolicyState.BuildContractManifestCodecs(contractPolicy.Codecs);
        var standaloneDiagnostics = HasNativeCodecRoute(compilation.Assembly)
            ? standalone.Diagnostics.Where(static diagnostic =>
                diagnostic.Kind is not (DtoDiagnosticKind.BuiltinAdapterOverride or DtoDiagnosticKind.BuiltinCustomCodecOverride))
            : standalone.Diagnostics;
        var diagnostics = standaloneDiagnostics
            .Concat(contractPolicy.Diagnostics)
            .Select(diagnostic => NormalizeExplicitBindingDiagnostic(compilation, diagnostic, cancellationToken))
            .GroupBy(static item => (item.Kind, item.TypeName, item.Detail))
            .Select(static group => group.First())
            .ToImmutableArray();
        var enums = standalone.Enums
            .Concat(contractDefault.Enums)
            .Concat(contractPolicy.Enums)
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
                // Explicit provenance can owner-scope a definition that is byte-for-byte identical
                // to the intrinsic selector/default definition. In that case both manifest tables
                // can reference the same generated factory: ownership comes from ContractCodecs,
                // not from manufacturing a duplicate implementation type. A real definition delta
                // still needs a distinct generated type because both versions coexist in one source.
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

        // Before direct Codec support, a plain class selected by RpcCodecAdapter meant "adapter that
        // forgot registration". Preserve that diagnostic unless WireFormatId makes direct-Codec
        // intent explicit.
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
            CollectAssemblyCustomCodecBindings();
            if (!selectorOnlyContractDefault)
                CollectAssemblyBindingsWithEnumSupport();
            if (_contractMode && !selectorOnlyContractDefault)
                CollectAssemblyRoutes();
        }

        internal DtoAnalysisPassResult AnalyzeWithFinalCodecBindings()
        {
            _ = Analyze();
            PromoteSelectedFixedMembersToCodecBindings();
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

            var reachable = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var root in roots.Values)
                CollectFinalBindingTypes(root, reachable, seen, 0);

            var result = ImmutableArray.CreateBuilder<GeneratedCodecModel>();
            foreach (var pair in reachable.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                if (selectedByType.TryGetValue(pair.Key, out var selected))
                {
                    result.Add(selected);
                    continue;
                }

                if (TryCreateImplicitContractManifestCodec(pair.Value, out var implicitCodec))
                    result.Add(implicitCodec);
            }
            return result.ToImmutable();
        }

        private bool TryCreateImplicitContractManifestCodec(
            ITypeSymbol type,
            out GeneratedCodecModel codec)
        {
            // Compatibility-only identities: the manifest records the implicit final selection
            // (deterministic Native builtin path or unmanaged UnsafeBlit fallback) so a later
            // explicit Adapter/Direct/Custom selection for the same closed type is detected as a
            // wire break. These identities are never passed to the runtime Codec emitter.
            if (!IsBuiltin(type))
            {
                codec = null!;
                return false;
            }

            var typeName = GetTypeName(type);
            var kind = IsNativeCodecType(type)
                ? GeneratedCodecKind.Native
                : GeneratedCodecKind.UnsafeBlit;
            var schemaIdentity = kind == GeneratedCodecKind.Native
                ? "implicit-native"
                : "implicit-unsafe-blit";
            if (type.TypeKind == TypeKind.Enum &&
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
                    !(_contractMode && HasNativeCodecRoute(_compilation.Assembly)))
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
            if (_contractMode)
                CollectReferencedContractRoots(roots);

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
                    if (member.Kind is not (GeneratedMemberKind.Fixed or GeneratedMemberKind.NullableFixed) ||
                        !memberSymbols.TryGetValue(member.Name, out var memberSymbol))
                    {
                        continue;
                    }

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
