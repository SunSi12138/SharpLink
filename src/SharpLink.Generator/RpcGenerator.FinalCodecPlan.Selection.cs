namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private FinalCodecPlan ResolveFinalCodecPlan(
            ITypeSymbol type,
            Dictionary<string, FinalCodecPlan> plans,
            HashSet<string> resolving)
        {
            var typeName = GetTypeName(type);
            if (plans.TryGetValue(typeName, out var existing))
                return existing;
            if (!resolving.Add(typeName))
            {
                throw new InvalidOperationException(
                    $"Final Codec graph contains an unresolved recursive Codec selection at '{typeName}'.");
            }

            _models.TryGetValue(typeName, out var generatedModel);
            FinalCodecPlan plan;
            if (generatedModel is { Kind: GeneratedCodecKind.Custom or GeneratedCodecKind.Adapter })
            {
                plan = ResolveGeneratedCodecPlan(type, generatedModel, plans, resolving);
            }
            else if (TryGetReferencedGeneratedCodecHash(type, out var referencedHash))
            {
                plan = new FinalReferencedCodecPlan(typeName, referencedHash);
            }
            else if (type.TypeKind == TypeKind.Enum &&
                     type is INamedTypeSymbol { EnumUnderlyingType: { } underlying } enumType)
            {
                ResolveFinalCodecPlan(underlying, plans, resolving);
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
        }

        private FinalCodecPlan ResolveGeneratedCodecPlan(
            ITypeSymbol type,
            GeneratedCodecModel model,
            Dictionary<string, FinalCodecPlan> plans,
            HashSet<string> resolving)
        {
            switch (model.Kind)
            {
                case GeneratedCodecKind.Custom:
                    return new FinalCustomCodecPlan(
                        model.TypeName,
                        GetRequiredOpaqueSemanticIdentity(model.CustomCodecType, "custom Codec"),
                        model.CustomCodecType ?? throw new InvalidOperationException(
                            $"Final custom Codec plan '{model.TypeName}' is missing its implementation binding."));
                case GeneratedCodecKind.Adapter:
                    return new FinalAdapterCodecPlan(
                        model.TypeName,
                        GetRequiredOpaqueSemanticIdentity(model.AdapterType, "Codec Adapter"),
                        GetAdapterTargetLogicalIdentity(type),
                        model.AdapterType ?? throw new InvalidOperationException(
                            $"Final Codec Adapter plan '{model.TypeName}' is missing its implementation binding."),
                        model.AdapterId ?? throw new InvalidOperationException(
                            $"Final Codec Adapter plan '{model.TypeName}' is missing its adapter identity."));
                case GeneratedCodecKind.Dto:
                    return ResolveGeneratedDtoPlan(type, model, plans, resolving);
                default:
                    return ResolveGeneratedCollectionPlan(type, model, plans, resolving);
            }
        }

        private FinalGeneratedDtoCodecPlan ResolveGeneratedDtoPlan(
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
                switch (member.Kind)
                {
                    case GeneratedMemberKind.String:
                        members.Add(CreateMember(
                            member,
                            FinalDtoMemberWireStrategy.String,
                            "string/content/utf16le/i32le-byte-length/v1|string/null/dto-wire-null/v1",
                            null));
                        break;
                    case GeneratedMemberKind.Fixed:
                    case GeneratedMemberKind.NullableFixed:
                        members.Add(CreateMember(
                            member,
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
                        members.Add(CreateMember(
                            member,
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
                FinalDtoMemberWireStrategy strategy,
                string? wireSemantic,
                string? childType)
                => new(
                    member.FieldId,
                    member.Kind,
                    member.Required,
                    member.Nullable,
                    member.NonNullableReference,
                    strategy,
                    wireSemantic,
                    childType);
        }

        private FinalCollectionCodecPlan ResolveGeneratedCollectionPlan(
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
            ResolveChild(element, model.ElementType);
            ResolveChild(key, model.KeyType);
            ResolveChild(value, model.ValueType);
            return new FinalCollectionCodecPlan(
                model.TypeName,
                model.Kind,
                FinalCollectionWireStrategy.ChildCodec,
                model.ElementType,
                model.KeyType,
                model.ValueType,
                RawElementLayout: null,
                StrategySemantic: null);

            void ResolveChild(ITypeSymbol? symbol, string? childTypeName)
            {
                if (childTypeName is null)
                    return;
                if (symbol is null && !TryResolveReachableType(childTypeName, out symbol!))
                {
                    throw new InvalidOperationException(
                        $"Final Codec plan for '{model.TypeName}' cannot resolve child '{childTypeName}'.");
                }
                ResolveFinalCodecPlan(symbol, plans, resolving);
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

        private bool TryGetReferencedGeneratedCodecHash(ITypeSymbol type, out RpcHashValue hash)
        {
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
                hash = new RpcHashValue(high, low);
                return true;
            }
            hash = default;
            return false;
        }

        private RpcHashValue GetRequiredOpaqueSemanticIdentity(
            string? implementationTypeName,
            string implementationKind)
        {
            if (TryGetOpaqueSemanticIdentity(implementationTypeName, out var hash))
                return hash;
            throw new InvalidOperationException(
                $"Opaque {implementationKind} '{implementationTypeName ?? "<unknown>"}' must declare [RpcCodecSemanticIdentity(high, low)].");
        }

        private bool TryGetOpaqueSemanticIdentity(string? implementationTypeName, out RpcHashValue hash)
        {
            if (implementationTypeName is null)
            {
                hash = default;
                return false;
            }
            if (_opaqueSemanticIdentityCache.TryGetValue(implementationTypeName, out var cached))
            {
                hash = cached ?? default;
                return cached.HasValue;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<IAssemblySymbol>();
            pending.Enqueue(_compilation.Assembly);
            while (pending.Count != 0)
            {
                var assembly = pending.Dequeue();
                if (!visited.Add(assembly.Identity.ToString()))
                    continue;
                if (TryFindNamedType(assembly.GlobalNamespace, implementationTypeName, out var implementationType))
                {
                    var attribute = implementationType.GetAttributes().FirstOrDefault(static item =>
                        IsAttribute(item, "SharpLink.Sdk", "RpcCodecSemanticIdentityAttribute"));
                    if (attribute is not null &&
                        attribute.ConstructorArguments.Length == 2 &&
                        attribute.ConstructorArguments[0].Value is ulong high &&
                        attribute.ConstructorArguments[1].Value is ulong low)
                    {
                        hash = new RpcHashValue(high, low);
                        _opaqueSemanticIdentityCache[implementationTypeName] = hash;
                        return true;
                    }
                }
                foreach (var referenced in assembly.Modules.SelectMany(static module => module.ReferencedAssemblySymbols))
                    pending.Enqueue(referenced);
            }

            _opaqueSemanticIdentityCache[implementationTypeName] = null;
            hash = default;
            return false;
        }

        private static bool TryFindNamedType(
            INamespaceSymbol namespaceSymbol,
            string typeName,
            out INamedTypeSymbol type)
        {
            foreach (var candidate in namespaceSymbol.GetTypeMembers())
            {
                if (TryFindNamedType(candidate, typeName, out type))
                    return true;
            }
            foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
            {
                if (TryFindNamedType(nestedNamespace, typeName, out type))
                    return true;
            }
            type = null!;
            return false;
        }

        private static bool TryFindNamedType(
            INamedTypeSymbol candidate,
            string typeName,
            out INamedTypeSymbol type)
        {
            if (string.Equals(GetTypeName(candidate), typeName, StringComparison.Ordinal))
            {
                type = candidate;
                return true;
            }
            foreach (var nested in candidate.GetTypeMembers())
            {
                if (TryFindNamedType(nested, typeName, out type))
                    return true;
            }
            type = null!;
            return false;
        }

        private bool TryResolveReachableType(string typeName, out ITypeSymbol type)
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
