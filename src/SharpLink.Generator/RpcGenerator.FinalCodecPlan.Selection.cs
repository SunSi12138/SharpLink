namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private FinalCodecPlan? ResolveFinalCodecPlan(
            ITypeSymbol type,
            Dictionary<string, FinalCodecPlan> plans,
            HashSet<string> resolving)
        {
            var typeName = GetTypeName(type);
            if (plans.TryGetValue(typeName, out var existing))
                return existing;
            if (_failed.Contains(typeName))
                return null;
            if (!resolving.Add(typeName))
            {
                throw new InvalidOperationException(
                    $"Final Codec graph contains an unresolved recursive Codec selection at '{typeName}'.");
            }

            if (TryResolvePolicyCodecPlan(type, out var policyPlan))
            {
                resolving.Remove(typeName);
                if (policyPlan is not null)
                    plans[typeName] = policyPlan;
                return policyPlan;
            }

            _models.TryGetValue(typeName, out var generatedModel);
            FinalCodecPlan? plan;
            if (TryGetReferencedGeneratedCodecHash(
                    type,
                    out var referencedHash,
                    out var incompatibleReferencedAbi))
            {
                plan = new FinalReferencedCodecPlan(typeName, referencedHash);
            }
            else if (incompatibleReferencedAbi)
            {
                return FailCurrent();
            }
            else if (type.TypeKind == TypeKind.Enum &&
                     type is INamedTypeSymbol { EnumUnderlyingType: { } underlying } enumType)
            {
                if (ResolveFinalCodecPlan(underlying, plans, resolving) is null)
                    return FailCurrent();
                plan = new FinalEnumCodecPlan(
                    typeName,
                    GetTypeName(underlying),
                    GetEnumDeclarationSemanticIdentity(enumType));
            }
            else if (type is INamedTypeSymbol nullable &&
                     nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                     nullable.TypeArguments.Length == 1 &&
                     HasExactBuiltinNullableCodecElement(nullable.TypeArguments[0]))
            {
                var child = ResolveFinalCodecPlan(nullable.TypeArguments[0], plans, resolving);
                if (child is null)
                    return FailCurrent();
                plan = new FinalPrimitiveCodecPlan(
                    typeName,
                    "nullable",
                    ImmutableArray<string>.Empty,
                    child.TypeName);
            }
            else if (TryGetFrameworkScalarSemantic(type, out var scalarSemantic))
            {
                plan = new FinalPrimitiveCodecPlan(typeName, "framework", scalarSemantic);
            }
            else if (TryGetCollection(
                         type,
                         out var collectionKind,
                         out var elementType,
                         out _,
                         out _))
            {
                if (generatedModel is not null)
                {
                    if (generatedModel.Kind is GeneratedCodecKind.Dto or
                        GeneratedCodecKind.Custom or
                        GeneratedCodecKind.Adapter)
                    {
                        throw new InvalidOperationException(
                            $"Final collection selection for '{typeName}' received incompatible generated candidate kind '{generatedModel.Kind}'.");
                    }
                    plan = ResolveGeneratedCodecPlan(type, generatedModel, plans, resolving);
                    if (plan is null)
                        return FailCurrent();
                }
                else if (collectionKind == GeneratedCodecKind.Nullable &&
                         elementType is not null &&
                         type.IsUnmanagedType &&
                         !HasExactBuiltinNullableCodecElement(elementType))
                {
                    plan = ResolveUnsafeBlitCodecPlan(type);
                }
                else if (TryResolveBuiltinCollectionPlan(
                             typeName,
                             collectionKind,
                             elementType,
                             out var builtinCollection))
                {
                    plan = builtinCollection;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Final RPC Codec graph has no generated or runtime builtin collection selection for '{typeName}'.");
                }
            }
            else if (type.IsUnmanagedType && !IsRuntimeSizedUnsafeBlitType(type))
            {
                plan = ResolveUnsafeBlitCodecPlan(type);
            }
            else if (generatedModel is { Kind: GeneratedCodecKind.Dto })
            {
                plan = ResolveGeneratedCodecPlan(type, generatedModel, plans, resolving);
                if (plan is null)
                    return FailCurrent();
            }
            else if (generatedModel is not null)
            {
                throw new InvalidOperationException(
                    $"Final RPC Codec graph received unsupported generated candidate kind '{generatedModel.Kind}' for '{typeName}'.");
            }
            else
            {
                throw new InvalidOperationException(
                    $"Final RPC Codec graph cannot resolve deterministic Codec semantics for '{typeName}'. Rebuild referenced SharpLink assemblies with deterministic identity generation enabled or bind an explicit Codec.");
            }

            resolving.Remove(typeName);
            plans[typeName] = plan;
            return plan;

            FinalCodecPlan? FailCurrent()
            {
                resolving.Remove(typeName);
                _failed.Add(typeName);
                return null;
            }
        }

        private bool TryResolvePolicyCodecPlan(
            ITypeSymbol type,
            out FinalCodecPlan? plan)
        {
            var typeName = GetTypeName(type);
            if (TrySelectCustomCodec(type, out var customCodec))
            {
                if (customCodec is null)
                {
                    plan = null;
                    return true;
                }

                var model = CreateCustomCodecModel(type, typeName, customCodec);
                _models[typeName] = model;
                plan = new FinalCustomCodecPlan(
                    typeName,
                    GetRequiredOpaqueSemanticIdentity(customCodec.CodecType, "custom Codec"),
                    GetCustomCodecTargetLogicalIdentity(type),
                    GetTypeName(customCodec.CodecType));
                return true;
            }

            if (!_applyCodecPolicy)
            {
                plan = null;
                return false;
            }

            AdapterRegistration? selectedAdapter = null;
            var hasSelection = _contractMode
                ? TrySelectContractCodecOverride(type, out selectedAdapter)
                : TrySelectAdapter(type, out selectedAdapter);
            if (!hasSelection)
            {
                plan = null;
                return false;
            }
            if (selectedAdapter is null)
            {
                plan = null;
                return true;
            }

            AddAdapterModel(type, typeName, selectedAdapter);
            plan = new FinalAdapterCodecPlan(
                typeName,
                GetRequiredOpaqueSemanticIdentity(selectedAdapter.AdapterType, "Codec Adapter"),
                GetAdapterTargetLogicalIdentity(type),
                GetTypeName(selectedAdapter.AdapterType),
                selectedAdapter.AdapterId);
            return true;
        }

        private GeneratedCodecModel CreateCustomCodecModel(

            ITypeSymbol type,
            string typeName,
            CustomCodecRegistration customCodec)
            => new(
                typeName,
                GetCodecName(typeName, _contractMode),
                GetSchemaId(typeName, "custom|" + GetTypeName(customCodec.CodecType)),
                GeneratedCodecKind.Custom,
                type.IsReferenceType,
                ImmutableArray<GeneratedMemberModel>.Empty,
                ImmutableArray<string>.Empty,
                null,
                null,
                null,
                GetTypeName(customCodec.CodecType),
                null,
                null,
                string.Empty,
                GetAssemblyDependencies([type]),
                type.Locations.FirstOrDefault());

        private bool HasCodecPolicyCandidate(ITypeSymbol type)
        {
            var normalized = NormalizeAdapterTarget(type);
            if (type.GetAttributes().Any(static attribute =>
                    IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAttribute")) ||
                _customCodecBindings.ContainsKey(normalized))
            {
                return true;
            }

            if (!_applyCodecPolicy)
                return false;

            var attributes = type.GetAttributes();
            var hasSelector = attributes.Any(attribute =>
                attribute.AttributeClass is { } attributeClass &&
                _adaptersBySelector.ContainsKey(attributeClass));
            if (_contractMode && _selectorOnlyContractDefaults)
                return hasSelector;

            if (hasSelector ||
                attributes.Any(static attribute =>
                    IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAdapterAttribute")) ||
                _assemblyBindings.ContainsKey(normalized))
            {
                return true;
            }

            return _contractMode && HasMatchingAssemblyRoute(type);
        }

        private bool HasCompositeCodecPolicyCandidate(ITypeSymbol type)
        {
            if (!TryGetCollection(type, out _, out var elementType, out var keyType, out var valueType))
                return false;
            return (elementType is not null && HasCodecPolicyCandidate(elementType)) ||
                   (keyType is not null && HasCodecPolicyCandidate(keyType)) ||
                   (valueType is not null && HasCodecPolicyCandidate(valueType));
        }

        private FinalCodecPlan? ResolveGeneratedCodecPlan(
            ITypeSymbol type,
            GeneratedCodecModel model,
            Dictionary<string, FinalCodecPlan> plans,
            HashSet<string> resolving)
        {
            switch (model.Kind)
            {
                case GeneratedCodecKind.Custom:
                case GeneratedCodecKind.Adapter:
                    throw new InvalidOperationException(
                        $"Final policy Codec plan '{model.TypeName}' must be resolved from the selected implementation symbol.");
                case GeneratedCodecKind.Dto:
                    return ResolveGeneratedDtoPlan(type, model, plans, resolving);
                default:
                    return ResolveGeneratedCollectionPlan(type, model, plans, resolving);
            }
        }

        private FinalGeneratedDtoCodecPlan? ResolveGeneratedDtoPlan(
            ITypeSymbol type,
            GeneratedCodecModel model,
            Dictionary<string, FinalCodecPlan> plans,
            HashSet<string> resolving)
        {
            var memberSymbols = type is INamedTypeSymbol named
                ? GetSerializableMembers(named).ToDictionary(static item => item.Name, StringComparer.Ordinal)
                : new Dictionary<string, ISymbol>(StringComparer.Ordinal);
            var members = ImmutableArray.CreateBuilder<FinalDtoMemberPlan>(model.Members.Length);
            foreach (var member in model.Members.OrderBy(static item => item.FieldId))
            {
                memberSymbols.TryGetValue(member.Name, out var memberSymbol);
                var memberType = memberSymbol is null ? null : GetMemberType(memberSymbol);

                if (memberType is not null && HasCodecPolicyCandidate(memberType))
                {
                    var selectedChild = ResolveFinalCodecPlan(memberType, plans, resolving);
                    if (selectedChild is null)
                        return null;
                    if (selectedChild is FinalCustomCodecPlan or FinalAdapterCodecPlan)
                    {
                        members.Add(CreateMember(
                            member,
                            GeneratedMemberKind.Complex,
                            FinalDtoMemberWireStrategy.ChildCodec,
                            null,
                            selectedChild.TypeName));
                        continue;
                    }
                }

                switch (member.Kind)
                {
                    case GeneratedMemberKind.String:
                        members.Add(CreateMember(
                            member,
                            member.Kind,
                            FinalDtoMemberWireStrategy.String,
                            "string/content/utf16le/i32le-byte-length/v1|string/null/dto-wire-null/v1",
                            null));
                        break;
                    case GeneratedMemberKind.Fixed:
                    case GeneratedMemberKind.NullableFixed:
                        members.Add(CreateMember(
                            member,
                            member.Kind,
                            FinalDtoMemberWireStrategy.Fixed,
                            GetResolvedFixedMemberSemantic(member, memberType),
                            null));
                        break;
                    case GeneratedMemberKind.Complex:
                        if (memberType is null && !TryResolveReachableType(member.TypeName, out memberType!))
                        {
                            throw new InvalidOperationException(
                                $"Final Codec plan for '{model.TypeName}' cannot resolve child '{member.TypeName}'.");
                        }
                        var child = ResolveFinalCodecPlan(memberType, plans, resolving);
                        if (child is null)
                            return null;
                        members.Add(CreateMember(
                            member,
                            member.Kind,
                            FinalDtoMemberWireStrategy.ChildCodec,
                            null,
                            child.TypeName));
                        break;
                }
            }

            return new FinalGeneratedDtoCodecPlan(
                model.TypeName,
                model.IsReferenceType,
                members.ToImmutable());

            static FinalDtoMemberPlan CreateMember(
                GeneratedMemberModel member,
                GeneratedMemberKind kind,
                FinalDtoMemberWireStrategy strategy,
                string? wireSemantic,
                string? childType)
                => new(
                    member.FieldId,
                    kind,
                    member.Required,
                    member.Nullable,
                    member.NonNullableReference,
                    strategy,
                    wireSemantic,
                    childType);
        }

        private FinalCollectionCodecPlan? ResolveGeneratedCollectionPlan(
            ITypeSymbol type,
            GeneratedCodecModel model,
            Dictionary<string, FinalCodecPlan> plans,
            HashSet<string> resolving)
        {
            ITypeSymbol? element = null;
            ITypeSymbol? key = null;
            ITypeSymbol? value = null;
            if (TryGetCollection(type, out _, out var resolvedElement, out var resolvedKey, out var resolvedValue))
            {
                element = resolvedElement;
                key = resolvedKey;
                value = resolvedValue;
            }
            if (!ResolveChild(element, model.ElementType) ||
                !ResolveChild(key, model.KeyType) ||
                !ResolveChild(value, model.ValueType))
            {
                return null;
            }
            return new FinalCollectionCodecPlan(
                model.TypeName,
                model.Kind,
                FinalCollectionWireStrategy.ChildCodec,
                model.ElementType,
                model.KeyType,
                model.ValueType,
                RawElementLayout: null,
                StrategySemantic: null);

            bool ResolveChild(ITypeSymbol? symbol, string? childTypeName)
            {
                if (childTypeName is null)
                    return true;
                if (symbol is null && !TryResolveReachableType(childTypeName, out symbol!))
                {
                    throw new InvalidOperationException(
                        $"Final Codec plan for '{model.TypeName}' cannot resolve child '{childTypeName}'.");
                }
                return ResolveFinalCodecPlan(symbol, plans, resolving) is not null;
            }
        }

        private bool TryResolveBuiltinCollectionPlan(
            string typeName,
            GeneratedCodecKind collectionKind,
            ITypeSymbol? elementType,
            out FinalCollectionCodecPlan plan)
        {
            if (elementType is null ||
                collectionKind is not (GeneratedCodecKind.Array or
                    GeneratedCodecKind.List or
                    GeneratedCodecKind.Memory or
                    GeneratedCodecKind.ReadOnlyMemory or
                    GeneratedCodecKind.ImmutableArray))
            {
                plan = null!;
                return false;
            }

            var elementTypeName = elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (elementTypeName.StartsWith("global::", StringComparison.Ordinal))
                elementTypeName = elementTypeName.Substring("global::".Length);
            if (!global::SharpLink.RpcBuiltinCollectionWireCatalog.TryGet(elementTypeName, out var descriptor))
            {
                plan = null!;
                return false;
            }

            switch (descriptor.Strategy)
            {
                case global::SharpLink.RpcBuiltinCollectionWireStrategy.DateTimeOffsetCanonical:
                    plan = new FinalCollectionCodecPlan(
                        typeName,
                        collectionKind,
                        FinalCollectionWireStrategy.DateTimeOffsetCanonical,
                        GetTypeName(elementType),
                        null,
                        null,
                        RawElementLayout: null,
                        StrategySemantic: descriptor.Semantic);
                    return true;
                case global::SharpLink.RpcBuiltinCollectionWireStrategy.RawBlit:
                    plan = new FinalCollectionCodecPlan(
                        typeName,
                        collectionKind,
                        FinalCollectionWireStrategy.RawBlit,
                        GetTypeName(elementType),
                        null,
                        null,
                        ResolvePhysicalLayout(
                            elementType,
                            GetTypeName(elementType),
                            collectAutoLayoutHazards: false,
                            null),
                        StrategySemantic: descriptor.Semantic);
                    return true;
                default:
                    throw new InvalidOperationException(
                        $"Unknown builtin collection wire strategy '{descriptor.Strategy}'.");
            }
        }

        private string GetResolvedFixedMemberSemantic(
            GeneratedMemberModel member,
            ITypeSymbol? actualMemberType)
        {
            var semanticType = actualMemberType;
            if (semanticType is INamedTypeSymbol nullable &&
                nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                nullable.TypeArguments.Length == 1)
            {
                semanticType = nullable.TypeArguments[0];
            }

            if (semanticType is not null &&
                string.Equals(semanticType.ToDisplayString(), "System.DateTimeOffset", StringComparison.Ordinal))
            {
                return "datetime-offset/dto-offset-minutes-i16le-padding6-utc-ticks-i64le/v1";
            }
            if (semanticType is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                return string.Join(
                    ":",
                    "fixed/v1",
                    member.FixedSize.ToString(InvariantCulture),
                    GetEnumDeclarationSemanticIdentity(enumType));
            }

            return string.Join(
                ":",
                "fixed/v1",
                member.FixedSize.ToString(InvariantCulture),
                member.FixedTypeName ?? member.EnumUnderlyingType ?? member.TypeName);
        }

        private static string GetEnumDeclarationSemanticIdentity(INamedTypeSymbol enumType)
        {
            var parts = new List<string>
            {
                "enum-declaration/v1",
                enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                enumType.EnumUnderlyingType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            };
            foreach (var field in enumType.GetMembers()
                         .OfType<IFieldSymbol>()
                         .Where(static field => field.HasConstantValue)
                         .OrderBy(static field => field.Name, StringComparer.Ordinal))
            {
                parts.Add(field.Name + "=" + Convert.ToString(field.ConstantValue, InvariantCulture));
            }
            return string.Join("|", parts);
        }

        private static bool HasExactBuiltinNullableCodecElement(ITypeSymbol type)
            => type.TypeKind != TypeKind.Enum && GetFixedSize(type) != 0;

        private static bool TryGetFrameworkScalarSemantic(
            ITypeSymbol type,
            out ImmutableArray<string> semantic)
        {
            if (type.SpecialType == SpecialType.System_String)
            {
                semantic = ImmutableArray.Create(
                    "string/content/utf16le/i32le-byte-length/v1",
                    "string/null/i32-minus-one/v1");
                return true;
            }

            string? token = type.SpecialType switch
            {
                SpecialType.System_Boolean => "bool/fixed1/v1",
                SpecialType.System_Byte => "u8/fixed1/v1",
                SpecialType.System_SByte => "i8/fixed1/v1",
                SpecialType.System_Int16 => "i16/fixed2/v1",
                SpecialType.System_UInt16 => "u16/fixed2/v1",
                SpecialType.System_Char => "char/fixed2/v1",
                SpecialType.System_Int32 => "i32/fixed4/v1",
                SpecialType.System_UInt32 => "u32/fixed4/v1",
                SpecialType.System_Single => "f32/fixed4/v1",
                SpecialType.System_Int64 => "i64/fixed8/v1",
                SpecialType.System_UInt64 => "u64/fixed8/v1",
                SpecialType.System_Double => "f64/fixed8/v1",
                SpecialType.System_Decimal => "decimal/fixed16/v1",
                _ => null
            };
            token ??= type.ToDisplayString() switch
            {
                "System.Half" => "half/fixed2/v1",
                "System.Text.Rune" => "rune/fixed4/v1",
                "System.Guid" => "guid/fixed16/v1",
                "System.DateTimeOffset" => "datetime-offset/root-ticks-i64le-offset-minutes-i16le/v1",
                "System.DateTime" => "datetime/fixed8/v1",
                "System.DateOnly" => "date-only/fixed4/v1",
                "System.TimeOnly" => "time-only/fixed8/v1",
                "System.TimeSpan" => "timespan/fixed8/v1",
                "System.Int128" => "i128/fixed16/v1",
                "System.UInt128" => "u128/fixed16/v1",
                "System.Index" => "index/fixed4/v1",
                "System.Range" => "range/fixed8/v1",
                _ => null
            };
            semantic = token is null ? ImmutableArray<string>.Empty : ImmutableArray.Create(token);
            return token is not null;
        }

        private bool TryGetReferencedGeneratedCodecHash(
            ITypeSymbol type,
            out RpcHashValue hash,
            out bool incompatibleAbi)
        {
            incompatibleAbi = false;
            var assembly = type.ContainingAssembly;
            if (assembly is null || SymbolEqualityComparer.Default.Equals(assembly, _compilation.Assembly))
            {
                hash = default;
                return false;
            }

            foreach (var attribute in assembly.GetAttributes())
            {
                if (!IsAttribute(attribute, "SharpLink.Abstractions", "SharpLinkGeneratedCodecIdentityAttribute") ||
                    attribute.ConstructorArguments.Length != 3 ||
                    attribute.ConstructorArguments[0].Value is not ITypeSymbol targetType ||
                    !SymbolEqualityComparer.Default.Equals(targetType, type) ||
                    attribute.ConstructorArguments[1].Value is not ulong high ||
                    attribute.ConstructorArguments[2].Value is not ulong low)
                {
                    continue;
                }

                if (!HasCurrentGeneratedAbiIdentity(assembly))
                {
                    Report(
                        DtoDiagnosticKind.Unsupported,
                        type,
                        $"referenced assembly '{assembly.Identity.Name}' publishes generated CodecHash metadata from an incompatible SharpLink generated ABI. Rebuild/regenerate the referenced assembly with the current SharpLink SDK.");
                    hash = default;
                    incompatibleAbi = true;
                    return false;
                }

                hash = new RpcHashValue(high, low);
                return true;
            }

            hash = default;
            return false;
        }

        private static bool HasCurrentGeneratedAbiIdentity(IAssemblySymbol assembly)
        {
            foreach (var attribute in assembly.GetAttributes())
            {
                if (IsAttribute(attribute, "SharpLink.Abstractions", "SharpLinkGeneratedAssemblyManifestAttribute") &&
                    attribute.ConstructorArguments.Length >= 5 &&
                    attribute.ConstructorArguments[4].Value is string abiIdentity &&
                    string.Equals(abiIdentity, GeneratedAbiIdentity, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static RpcHashValue GetRequiredOpaqueSemanticIdentity(
            INamedTypeSymbol implementationType,
            string implementationKind)
        {
            var attribute = implementationType.OriginalDefinition.GetAttributes().FirstOrDefault(static item =>
                IsAttribute(item, "SharpLink.Sdk", "RpcCodecSemanticIdentityAttribute"));
            if (attribute is not null &&
                attribute.ConstructorArguments.Length == 2 &&
                attribute.ConstructorArguments[0].Value is ulong high &&
                attribute.ConstructorArguments[1].Value is ulong low)
            {
                return new RpcHashValue(high, low);
            }

            throw new InvalidOperationException(
                $"Opaque {implementationKind} '{GetTypeName(implementationType)}' must declare [RpcCodecSemanticIdentity(high, low)].");
        }

        private bool TryResolveReachableType(
string typeName, out ITypeSymbol type)
        {
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
            return reachable.TryGetValue(typeName, out type!);
        }

        private RpcHashValue GetCustomCodecTargetLogicalIdentity(ITypeSymbol targetType)
        {
            var parts = new List<string> { "custom-target/v1" };
            AppendClosedTargetLogicalIdentity(targetType, parts);
            return Hashing.GetSemanticHash(parts.ToArray());
        }

        private RpcHashValue GetAdapterTargetLogicalIdentity(ITypeSymbol targetType)
        {
            var parts = new List<string> { "adapter-target/v2" };
            AppendClosedTargetLogicalIdentity(targetType, parts);
            return Hashing.GetSemanticHash(parts.ToArray());
        }

        internal static IEnumerable<string> GetFinalCodecPlanDependencies(FinalCodecPlan plan)
        {
            switch (plan)
            {
                case FinalPrimitiveCodecPlan { ChildType: { } child }:
                    yield return child;
                    break;
                case FinalEnumCodecPlan enumPlan:
                    yield return enumPlan.UnderlyingType;
                    break;
                case FinalGeneratedDtoCodecPlan dto:
                    foreach (var member in dto.Members)
                    {
                        if (member.ChildType is not null)
                            yield return member.ChildType;
                    }
                    break;
                case FinalCollectionCodecPlan { WireStrategy: FinalCollectionWireStrategy.ChildCodec } collection:
                    if (collection.ElementType is not null) yield return collection.ElementType;
                    if (collection.KeyType is not null) yield return collection.KeyType;
                    if (collection.ValueType is not null) yield return collection.ValueType;
                    break;
            }
        }
    }
}
