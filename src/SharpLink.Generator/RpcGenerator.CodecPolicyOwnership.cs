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

        // Compatibility/runtime publication is rooted only in Contracts owned by the current
        // compilation. Referenced manifest-less [RpcContract] assemblies may still participate in
        // static conflict analysis, but their payload graph is not part of this assembly's Codec
        // ownership boundary and must not leak into either generated Codec table.
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

        // Contract ownership is defined by explicit/route/custom selection provenance as well as the
        // resulting definition delta. Provenance matters when an explicit binding intentionally
        // selects the same definition that the default analysis would choose: it is still published
        // Contract policy and must not become runtime-overridable merely because the definitions
        // happen to compare equal. Native parents/dependencies are then pulled into the owner graph
        // so the changed policy remains closed over its full generated dependency graph.
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

        // Explicit builtin bindings are authoritative only for payload types owned by the
        // current Contract graph. Preserve standalone diagnostics for unrelated [RpcSerializable]
        // graphs instead of suppressing them merely because the assembly owns some RPC Contract.
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
            if (!selectorOnlyContractDefault)
            {
                CollectAssemblyCustomCodecBindings();
                if (_contractMode)
                    CollectContractBuiltinCustomCodecBindings();
                CollectAssemblyBindingsWithEnumSupport();
            }
            if (_contractMode && !selectorOnlyContractDefault)
                CollectAssemblyRoutes();
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

            // This structural index is only a symbol lookup table. Publication itself follows the
            // final Codec graph below, so Custom/Adapter/Direct owners remain opaque and their CLR
            // implementation members cannot become phantom compatibility or ownership nodes.
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

                // Native composite fast paths inline their element/key/value representations, so
                // those child identities remain part of compatibility even though no provider
                // lookup occurs. UnsafeBlit is opaque: its own schema describes the complete raw
                // layout and must not recurse through CLR fields as independent Codec nodes.
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

        private static string GetUnsafeBlitSchemaIdentity(ITypeSymbol type)
        {
            var builder = new StringBuilder("implicit-unsafe-blit");
            AppendUnsafeBlitLayout(type, builder, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), 0);
            return builder.ToString();
        }

        private static void AppendUnsafeBlitLayout(
            ITypeSymbol type,
            StringBuilder builder,
            HashSet<ITypeSymbol> stack,
            int depth)
        {
            builder.Append('|').Append(GetTypeName(type));
            if (depth > MaximumDepth)
            {
                builder.Append("|depth-limit");
                return;
            }

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

            AppendWireLayoutAttribute(builder, named, "System.Runtime.InteropServices.StructLayoutAttribute");
            AppendWireLayoutAttribute(builder, named, "System.Runtime.CompilerServices.InlineArrayAttribute");

            if (!stack.Add(type))
            {
                builder.Append("|recursive");
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
                builder.Append("|field:").Append(index.ToString(InvariantCulture));
                AppendWireLayoutAttribute(builder, field, "System.Runtime.InteropServices.FieldOffsetAttribute");
                AppendWireLayoutAttribute(builder, field, "System.Runtime.CompilerServices.FixedBufferAttribute");
                AppendUnsafeBlitLayout(field.Type, builder, stack, depth + 1);
            }

            stack.Remove(type);
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
