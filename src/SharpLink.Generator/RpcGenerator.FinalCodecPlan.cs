namespace SharpLink.Generator;

internal enum FinalCodecPlanKind
{
    Primitive,
    Enum,
    GeneratedDto,
    Collection,
    UnsafeBlit,
    Custom,
    Adapter,
    Referenced
}

internal enum FinalCollectionWireStrategy
{
    ChildCodec,
    RawBlit,
    DateTimeOffsetCanonical
}

internal enum FinalEffectiveLayoutKind
{
    Sequential,
    Explicit,
    Auto
}

internal sealed record FinalUnsafeBlitAbiPlan(
    string Endianness,
    int NativePointerWidth,
    string Version);

internal abstract record FinalCodecPlan(string TypeName, FinalCodecPlanKind Kind);

internal sealed record FinalPrimitiveCodecPlan(
    string TypeName,
    string Family,
    ImmutableArray<string> SemanticParts,
    string? ChildType = null)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.Primitive);

internal sealed record FinalEnumCodecPlan(
    string TypeName,
    string UnderlyingType,
    string DeclarationSemantic)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.Enum);

internal enum FinalDtoMemberWireStrategy
{
    String,
    Fixed,
    ChildCodec
}

internal sealed record FinalDtoMemberPlan(
    uint FieldId,
    GeneratedMemberKind Kind,
    bool Required,
    bool Nullable,
    bool NonNullableReference,
    FinalDtoMemberWireStrategy WireStrategy,
    string? WireSemantic,
    string? ChildType);

internal sealed record FinalGeneratedDtoCodecPlan(
    string TypeName,
    bool IsReferenceType,
    ImmutableArray<FinalDtoMemberPlan> Members)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.GeneratedDto);

internal sealed record FinalCollectionCodecPlan(
    string TypeName,
    GeneratedCodecKind CollectionKind,
    FinalCollectionWireStrategy WireStrategy,
    string? ElementType,
    string? KeyType,
    string? ValueType,
    FinalPhysicalLayoutPlan? RawElementLayout,
    string? StrategySemantic)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.Collection);

internal sealed record FinalUnsafeBlitCodecPlan(
    string TypeName,
    FinalUnsafeBlitAbiPlan Abi,
    FinalPhysicalLayoutPlan Layout,
    ImmutableArray<FinalCodecAutoLayoutHazardDescriptor> AutoLayoutHazards)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.UnsafeBlit);

internal sealed record FinalCustomCodecPlan(
    string TypeName,
    RpcHashValue OpaqueSemanticIdentity)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.Custom);

internal sealed record FinalAdapterCodecPlan(
    string TypeName,
    RpcHashValue OpaqueSemanticIdentity,
    RpcHashValue ClosedTargetLogicalIdentity)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.Adapter);

internal sealed record FinalReferencedCodecPlan(
    string TypeName,
    RpcHashValue CodecHash)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.Referenced);

internal abstract record FinalPhysicalLayoutPlan;

internal sealed record FinalPrimitivePhysicalPlan(
    string Token,
    string? FrameworkRawAbi = null)
    : FinalPhysicalLayoutPlan;

internal sealed record FinalEnumPhysicalPlan(
    FinalPhysicalLayoutPlan Underlying,
    string DeclarationSemantic)
    : FinalPhysicalLayoutPlan;

internal sealed record FinalPointerPhysicalPlan(string TargetLogicalIdentity)
    : FinalPhysicalLayoutPlan;

internal sealed record FinalFunctionPointerPhysicalPlan(string SignatureSemantic)
    : FinalPhysicalLayoutPlan;

internal sealed record FinalFixedBufferPhysicalPlan(
    int Length,
    FinalPhysicalLayoutPlan Element)
    : FinalPhysicalLayoutPlan;

internal sealed record FinalPhysicalFieldPlan(
    int? Offset,
    FinalPhysicalLayoutPlan Layout);

internal sealed record FinalStructPhysicalPlan(
    FinalEffectiveLayoutKind LayoutKind,
    int Pack,
    int Size,
    int? InlineArrayLength,
    ImmutableArray<FinalPhysicalFieldPlan> Fields)
    : FinalPhysicalLayoutPlan;

internal readonly record struct FinalCodecAutoLayoutHazardDescriptor(
    string TypeName,
    string FieldPath,
    Location Location);

internal readonly record struct FinalCodecAutoLayoutDiagnosticModel(
    string PayloadType,
    string TypeName,
    string FieldPath,
    Location Location);

internal sealed class FinalCodecGraph(
    IReadOnlyDictionary<string, FinalCodecPlan> plans,
    ImmutableArray<string> rootTypes)
{
    internal IReadOnlyDictionary<string, FinalCodecPlan> Plans { get; } = plans;
    internal ImmutableArray<string> RootTypes { get; } = rootTypes;
}

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private static readonly FinalUnsafeBlitAbiPlan UnsafeBlitAbi =
            new("little-endian", 8, "v3");

        private readonly Dictionary<string, RpcHashValue?> _opaqueSemanticIdentityCache =
            new(StringComparer.Ordinal);

        internal FinalCodecGraph ResolveFinalCodecGraph(
            bool includeSerializable,
            bool includeContracts)
        {
            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(
                _compilation.Assembly.GlobalNamespace,
                roots,
                includeSerializable,
                includeContracts);

            var plans = new Dictionary<string, FinalCodecPlan>(StringComparer.Ordinal);
            var resolving = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in roots.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                if (!_failed.Contains(pair.Key))
                    ResolveFinalCodecPlan(pair.Value, plans, resolving);
            }

            // Candidate generation is intentionally allowed to discover factories before this pass.
            // Final selection is not: every emitted factory must be represented by the resolved graph.
            foreach (var model in _models.Values.OrderBy(static item => item.TypeName, StringComparer.Ordinal))
            {
                if (_failed.Contains(model.TypeName) || plans.ContainsKey(model.TypeName))
                    continue;
                if (TryResolveReachableType(model.TypeName, out var type))
                    ResolveFinalCodecPlan(type, plans, resolving);
            }

            return new FinalCodecGraph(
                plans,
                roots.Keys.Where(type => !_failed.Contains(type))
                    .OrderBy(static type => type, StringComparer.Ordinal)
                    .ToImmutableArray());
        }

        internal ImmutableArray<FinalCodecAutoLayoutDiagnosticModel> BuildUnsafeBlitAutoLayoutDiagnostics()
        {
            var graph = ResolveFinalCodecGraph(includeSerializable: false, includeContracts: true);
            var diagnostics = ImmutableArray.CreateBuilder<FinalCodecAutoLayoutDiagnosticModel>();
            var dedup = new HashSet<(string Payload, string Type, string Path)>();

            foreach (var payload in graph.RootTypes)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                Visit(payload);

                void Visit(string typeName)
                {
                    if (!visited.Add(typeName) || !graph.Plans.TryGetValue(typeName, out var plan))
                        return;
                    if (plan is FinalUnsafeBlitCodecPlan unsafeBlit)
                    {
                        foreach (var hazard in unsafeBlit.AutoLayoutHazards)
                        {
                            if (dedup.Add((payload, hazard.TypeName, hazard.FieldPath)))
                            {
                                diagnostics.Add(new FinalCodecAutoLayoutDiagnosticModel(
                                    payload,
                                    hazard.TypeName,
                                    hazard.FieldPath,
                                    hazard.Location));
                            }
                        }
                        return;
                    }

                    foreach (var dependency in GetFinalCodecPlanDependencies(plan))
                        Visit(dependency);
                }
            }

            return diagnostics
                .OrderBy(static item => item.PayloadType, StringComparer.Ordinal)
                .ThenBy(static item => item.TypeName, StringComparer.Ordinal)
                .ThenBy(static item => item.FieldPath, StringComparer.Ordinal)
                .ToImmutableArray();
        }

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

            FinalCodecPlan plan;
            if (_models.TryGetValue(typeName, out var generatedModel))
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
                plan = new FinalPrimitiveCodecPlan(
                    typeName,
                    "framework",
                    scalarSemantic);
            }
            else if (TryGetCollection(
                         type,
                         out var collectionKind,
                         out var elementType,
                         out var keyType,
                         out var valueType))
            {
                if (collectionKind == GeneratedCodecKind.Nullable &&
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
                        GetRequiredOpaqueSemanticIdentity(model.CustomCodecType, "custom Codec"));
                case GeneratedCodecKind.Adapter:
                    return new FinalAdapterCodecPlan(
                        model.TypeName,
                        GetRequiredOpaqueSemanticIdentity(model.AdapterType, "Codec Adapter"),
                        GetAdapterTargetLogicalIdentity(type));
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
                    GeneratedCodecKind.ImmutableArray) ||
                !IsBuiltinBlitElement(elementType))
            {
                plan = null!;
                return false;
            }

            if (string.Equals(elementType.ToDisplayString(), "System.DateTimeOffset", StringComparison.Ordinal))
            {
                plan = new FinalCollectionCodecPlan(
                    typeName,
                    collectionKind,
                    FinalCollectionWireStrategy.DateTimeOffsetCanonical,
                    GetTypeName(elementType),
                    null,
                    null,
                    RawElementLayout: null,
                    StrategySemantic: "datetime-offset/collection-raw16-padding2-7-zero/release-scoped/v1");
                return true;
            }

            plan = new FinalCollectionCodecPlan(
                typeName,
                collectionKind,
                FinalCollectionWireStrategy.RawBlit,
                GetTypeName(elementType),
                null,
                null,
                ResolvePhysicalLayout(elementType, GetTypeName(elementType), collectAutoLayoutHazards: false, null),
                StrategySemantic: "builtin-blit-element/v2|abi:little-endian");
            return true;
        }

        private FinalUnsafeBlitCodecPlan ResolveUnsafeBlitCodecPlan(ITypeSymbol type)
        {
            var hazards = ImmutableArray.CreateBuilder<FinalCodecAutoLayoutHazardDescriptor>();
            var typeName = GetTypeName(type);
            var layout = ResolvePhysicalLayout(type, typeName, collectAutoLayoutHazards: true, hazards);
            return new FinalUnsafeBlitCodecPlan(
                typeName,
                UnsafeBlitAbi,
                layout,
                hazards
                    .OrderBy(static item => item.TypeName, StringComparer.Ordinal)
                    .ThenBy(static item => item.FieldPath, StringComparer.Ordinal)
                    .ToImmutableArray());
        }

        private FinalPhysicalLayoutPlan ResolvePhysicalLayout(
            ITypeSymbol type,
            string fieldPath,
            bool collectAutoLayoutHazards,
            ImmutableArray<FinalCodecAutoLayoutHazardDescriptor>.Builder? hazards,
            HashSet<ITypeSymbol>? stack = null)
        {
            if (TryGetPhysicalPrimitive(type, out var primitive))
                return primitive;

            if (type.TypeKind == TypeKind.Enum &&
                type is INamedTypeSymbol { EnumUnderlyingType: { } underlying } enumType)
            {
                return new FinalEnumPhysicalPlan(
                    ResolvePhysicalLayout(underlying, fieldPath, false, null, stack),
                    GetEnumDeclarationSemanticIdentity(enumType));
            }

            if (type is IPointerTypeSymbol pointer)
            {
                var parts = new List<string> { "pointer-target/v1" };
                AppendClosedTargetLogicalIdentity(pointer.PointedAtType, parts);
                return new FinalPointerPhysicalPlan(Hashing.GetSemanticHash(parts.ToArray()).ToHex());
            }

            if (type is IFunctionPointerTypeSymbol functionPointer)
                return new FinalFunctionPointerPhysicalPlan(GetFunctionPointerSemanticIdentity(functionPointer));

            if (type is not INamedTypeSymbol named)
                throw new InvalidOperationException($"Unsupported unmanaged physical type '{GetTypeName(type)}'.");

            stack ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            if (!stack.Add(type))
                throw new InvalidOperationException($"Recursive unmanaged physical layout '{GetTypeName(type)}'.");

            var effective = GetEffectiveStructLayout(named);
            if (collectAutoLayoutHazards &&
                effective.Kind == FinalEffectiveLayoutKind.Auto &&
                SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, _compilation.Assembly))
            {
                var location = named.Locations.FirstOrDefault(static item => item.IsInSource)
                    ?? Location.None;
                if (location != Location.None)
                {
                    hazards?.Add(new FinalCodecAutoLayoutHazardDescriptor(
                        GetTypeName(named),
                        fieldPath,
                        location));
                }
            }

            var fields = ImmutableArray.CreateBuilder<FinalPhysicalFieldPlan>();
            foreach (var field in named.GetMembers().OfType<IFieldSymbol>()
                         .Where(static item => !item.IsStatic && !item.IsConst))
            {
                FinalPhysicalLayoutPlan fieldLayout;
                if (field.IsFixedSizeBuffer && TryGetFixedBufferElement(field, out var fixedElement))
                {
                    fieldLayout = new FinalFixedBufferPhysicalPlan(
                        field.FixedSize,
                        ResolvePhysicalLayout(fixedElement, fieldPath + "." + field.Name, false, null, stack));
                }
                else
                {
                    fieldLayout = ResolvePhysicalLayout(
                        field.Type,
                        fieldPath + "." + field.Name,
                        collectAutoLayoutHazards,
                        hazards,
                        stack);
                }

                var offset = effective.Kind == FinalEffectiveLayoutKind.Explicit
                    ? GetFieldOffset(field)
                    : null;
                fields.Add(new FinalPhysicalFieldPlan(offset, fieldLayout));
            }

            stack.Remove(type);
            var canonicalFields = fields.ToArray();
            if (effective.Kind == FinalEffectiveLayoutKind.Explicit)
            {
                Array.Sort(canonicalFields, static (left, right) =>
                {
                    var byOffset = Nullable.Compare(left.Offset, right.Offset);
                    if (byOffset != 0)
                        return byOffset;
                    return StringComparer.Ordinal.Compare(
                        GetPhysicalPlanSortKey(left.Layout),
                        GetPhysicalPlanSortKey(right.Layout));
                });
            }

            return new FinalStructPhysicalPlan(
                effective.Kind,
                effective.Pack,
                effective.Size,
                GetInlineArrayLength(named),
                canonicalFields.ToImmutableArray());
        }

        private static (FinalEffectiveLayoutKind Kind, int Pack, int Size) GetEffectiveStructLayout(
            INamedTypeSymbol type)
        {
            var kind = FinalEffectiveLayoutKind.Sequential;
            var pack = 0;
            var size = 0;
            var attribute = type.GetAttributes().FirstOrDefault(static item =>
                string.Equals(
                    item.AttributeClass?.ToDisplayString(),
                    "System.Runtime.InteropServices.StructLayoutAttribute",
                    StringComparison.Ordinal));
            if (attribute is null)
                return (kind, pack, size);

            if (attribute.ConstructorArguments.Length != 0 &&
                attribute.ConstructorArguments[0].Value is int layoutKind)
            {
                kind = layoutKind switch
                {
                    2 => FinalEffectiveLayoutKind.Explicit,
                    3 => FinalEffectiveLayoutKind.Auto,
                    _ => FinalEffectiveLayoutKind.Sequential
                };
            }
            foreach (var argument in attribute.NamedArguments)
            {
                if (argument.Value.Value is not int value)
                    continue;
                if (string.Equals(argument.Key, "Pack", StringComparison.Ordinal))
                    pack = value;
                else if (string.Equals(argument.Key, "Size", StringComparison.Ordinal))
                    size = value;
            }
            return (kind, pack, size);
        }

        private static int? GetInlineArrayLength(INamedTypeSymbol type)
        {
            var attribute = type.GetAttributes().FirstOrDefault(static item =>
                string.Equals(
                    item.AttributeClass?.ToDisplayString(),
                    "System.Runtime.CompilerServices.InlineArrayAttribute",
                    StringComparison.Ordinal));
            return attribute is { ConstructorArguments.Length: 1 } &&
                   attribute.ConstructorArguments[0].Value is int length
                ? length
                : null;
        }

        private static int GetFieldOffset(IFieldSymbol field)
        {
            var attribute = field.GetAttributes().FirstOrDefault(static item =>
                string.Equals(
                    item.AttributeClass?.ToDisplayString(),
                    "System.Runtime.InteropServices.FieldOffsetAttribute",
                    StringComparison.Ordinal));
            return attribute is { ConstructorArguments.Length: 1 } &&
                   attribute.ConstructorArguments[0].Value is int offset
                ? offset
                : 0;
        }

        private static bool TryGetFixedBufferElement(IFieldSymbol field, out ITypeSymbol elementType)
        {
            var attribute = field.GetAttributes().FirstOrDefault(static item =>
                string.Equals(
                    item.AttributeClass?.ToDisplayString(),
                    "System.Runtime.CompilerServices.FixedBufferAttribute",
                    StringComparison.Ordinal));
            if (attribute is { ConstructorArguments.Length: >= 1 } &&
                attribute.ConstructorArguments[0].Value is ITypeSymbol type)
            {
                elementType = type;
                return true;
            }
            elementType = null!;
            return false;
        }

        private static bool TryGetPhysicalPrimitive(
            ITypeSymbol type,
            out FinalPrimitivePhysicalPlan primitive)
        {
            string? token = type.SpecialType switch
            {
                SpecialType.System_Boolean => "bool1",
                SpecialType.System_Byte => "u8",
                SpecialType.System_SByte => "i8",
                SpecialType.System_Int16 => "i16",
                SpecialType.System_UInt16 => "u16",
                SpecialType.System_Char => "char16",
                SpecialType.System_Int32 => "i32",
                SpecialType.System_UInt32 => "u32",
                SpecialType.System_Single => "f32",
                SpecialType.System_Int64 => "i64",
                SpecialType.System_UInt64 => "u64",
                SpecialType.System_IntPtr => "native-pointer-width/64:intptr",
                SpecialType.System_UIntPtr => "native-pointer-width/64:uintptr",
                SpecialType.System_Double => "f64",
                SpecialType.System_Decimal => "decimal128",
                _ => null
            };
            string? frameworkRawAbi = null;
            if (token is null)
            {
                token = type.ToDisplayString() switch
                {
                    "System.Half" => "half16",
                    "System.Text.Rune" => "rune32",
                    "System.Guid" => "guid128",
                    "System.DateTimeOffset" => "datetimeoffset128",
                    "System.DateTime" => "datetime64",
                    "System.DateOnly" => "dateonly32",
                    "System.TimeOnly" => "timeonly64",
                    "System.TimeSpan" => "timespan64",
                    "System.Int128" => "i128",
                    "System.UInt128" => "u128",
                    "System.Index" => "index32",
                    "System.Range" => "range64",
                    _ => null
                };
                if (string.Equals(type.ToDisplayString(), "System.DateTimeOffset", StringComparison.Ordinal))
                    frameworkRawAbi = "framework-raw/datetimeoffset/native16/release-scoped/v1";
            }
            if (token is null)
            {
                primitive = null!;
                return false;
            }
            primitive = new FinalPrimitivePhysicalPlan(token, frameworkRawAbi);
            return true;
        }

        private static string GetFunctionPointerSemanticIdentity(IFunctionPointerTypeSymbol pointer)
        {
            var signature = pointer.Signature;
            var parts = new List<string>
            {
                "function-pointer/v2",
                signature.CallingConvention.ToString(),
                signature.RefKind.ToString()
            };
            foreach (var convention in signature.UnmanagedCallingConventionTypes
                         .OrderBy(static item => item.ToDisplayString(), StringComparer.Ordinal))
            {
                parts.Add(convention.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
            AppendClosedTargetLogicalIdentity(signature.ReturnType, parts);
            parts.Add(signature.Parameters.Length.ToString(InvariantCulture));
            foreach (var parameter in signature.Parameters)
            {
                parts.Add(parameter.RefKind.ToString());
                AppendClosedTargetLogicalIdentity(parameter.Type, parts);
            }
            return Hashing.GetSemanticHash(parts.ToArray()).ToHex();
        }

        private static string GetPhysicalPlanSortKey(FinalPhysicalLayoutPlan plan)
        {
            var parts = new List<string>();
            Append(plan, parts);
            return string.Join("|", parts);

            static void Append(FinalPhysicalLayoutPlan current, List<string> parts)
            {
                switch (current)
                {
                    case FinalPrimitivePhysicalPlan primitive:
                        parts.Add("p:" + primitive.Token + ":" + primitive.FrameworkRawAbi);
                        return;
                    case FinalEnumPhysicalPlan enumPlan:
                        parts.Add("e:" + enumPlan.DeclarationSemantic);
                        Append(enumPlan.Underlying, parts);
                        return;
                    case FinalPointerPhysicalPlan pointer:
                        parts.Add("ptr:" + pointer.TargetLogicalIdentity);
                        return;
                    case FinalFunctionPointerPhysicalPlan functionPointer:
                        parts.Add("fn:" + functionPointer.SignatureSemantic);
                        return;
                    case FinalFixedBufferPhysicalPlan buffer:
                        parts.Add("buf:" + buffer.Length.ToString(InvariantCulture));
                        Append(buffer.Element, parts);
                        return;
                    case FinalStructPhysicalPlan structure:
                        parts.Add($"s:{structure.LayoutKind}:{structure.Pack}:{structure.Size}:{structure.InlineArrayLength}");
                        foreach (var field in structure.Fields)
                        {
                            parts.Add("o:" + (field.Offset?.ToString(InvariantCulture) ?? "seq"));
                            Append(field.Layout, parts);
                        }
                        return;
                }
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

        private static IEnumerable<string> GetFinalCodecPlanDependencies(FinalCodecPlan plan)
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
