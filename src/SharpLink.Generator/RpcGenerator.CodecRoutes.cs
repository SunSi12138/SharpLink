namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private const int RpcCodecScopeManaged = 1 << 0;
    private const int RpcCodecScopeUnmanaged = 1 << 1;
    private const int RpcCodecScopeNative = 1 << 2;
    private const int RpcCodecScopeAll = RpcCodecScopeManaged | RpcCodecScopeUnmanaged | RpcCodecScopeNative;

    private static bool HasGeneratedAssemblyManifest(IAssemblySymbol assembly)
        => assembly.GetAttributes().Any(static attribute =>
            string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                GeneratedAssemblyManifestAttributeMetadataName,
                StringComparison.Ordinal));

    private static bool HasNativeCodecRoute(IAssemblySymbol assembly)
    {
        foreach (var attribute in assembly.GetAttributes())
        {
            if (!TryGetCodecRoute(attribute, out var scope, out _) ||
                scope <= 0 || (scope & ~RpcCodecScopeAll) != 0)
            {
                continue;
            }

            if ((scope & RpcCodecScopeNative) != 0)
                return true;
        }

        return false;
    }

    private static bool TryGetCodecRoute(
        AttributeData attribute,
        out int scope,
        out ITypeSymbol? adapterType)
    {
        scope = 0;
        adapterType = null;
        if (!IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecRouteAttribute") ||
            attribute.ConstructorArguments.Length != 2 ||
            attribute.ConstructorArguments[0].Value is not int value ||
            attribute.ConstructorArguments[1].Value is not ITypeSymbol adapter)
        {
            return false;
        }

        scope = value;
        adapterType = adapter;
        return true;
    }

    private sealed partial class DtoAnalysisState
    {
        private readonly Dictionary<int, ITypeSymbol> _assemblyRoutes = [];
        private readonly HashSet<int> _conflictingRouteScopes = [];
        private readonly HashSet<ITypeSymbol> _manifestScopedSelections = new(SymbolEqualityComparer.Default);
        private HashSet<ITypeSymbol>? _routeEligibleTypes;

        private void CollectAssemblyRoutes()
        {
            foreach (var attribute in _compilation.Assembly.GetAttributes()
                         .Where(static attribute => IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecRouteAttribute"))
                         .OrderBy(static attribute => attribute.ToString(), StringComparer.Ordinal))
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? Location.None;
                if (!TryGetCodecRoute(attribute, out var scope, out var adapterType) || adapterType is null)
                {
                    Report(
                        DtoDiagnosticKind.AdapterBindingInvalid,
                        _compilation.Assembly,
                        "assembly-level RpcCodecRoute requires RpcCodecScope and adapterType",
                        location);
                    continue;
                }

                if (scope <= 0 || (scope & ~RpcCodecScopeAll) != 0)
                {
                    Report(
                        DtoDiagnosticKind.AdapterBindingInvalid,
                        _compilation.Assembly,
                        $"RpcCodecRoute scope value '{scope}' must be a non-empty combination of Managed, Unmanaged, and Native",
                        location);
                    continue;
                }

                AddRouteBits(scope, adapterType, location);
            }
        }

        private void AddRouteBits(int scope, ITypeSymbol adapterType, Location location)
        {
            AddRouteBit(RpcCodecScopeManaged, "Managed");
            AddRouteBit(RpcCodecScopeUnmanaged, "Unmanaged");
            AddRouteBit(RpcCodecScopeNative, "Native");

            void AddRouteBit(int bit, string name)
            {
                if ((scope & bit) == 0)
                    return;
                if (!_assemblyRoutes.TryGetValue(bit, out var existing))
                {
                    _assemblyRoutes.Add(bit, adapterType);
                    return;
                }
                if (SymbolEqualityComparer.Default.Equals(existing, adapterType))
                    return;
                if (!_conflictingRouteScopes.Add(bit))
                    return;

                Report(
                    DtoDiagnosticKind.AdapterSelectionConflict,
                    _compilation.Assembly,
                    $"RpcCodecRoute declarations overlap for scope '{name}' with different adapters '{GetTypeName(existing)}' and '{GetTypeName(adapterType)}'",
                    location);
            }
        }

        private bool TrySelectRouteAdapter(ITypeSymbol type, out AdapterRegistration? selected)
        {
            selected = null;
            if (!IsRouteEligible(type))
                return false;

            var scope = ClassifyCodecScope(type);
            if (_conflictingRouteScopes.Contains(scope))
            {
                _failed.Add(GetTypeName(type));
                return true;
            }
            if (!_assemblyRoutes.TryGetValue(scope, out var adapterType))
                return false;
            if (!_adaptersByType.TryGetValue(adapterType, out selected))
            {
                Report(
                    DtoDiagnosticKind.AdapterRegistrationInvalid,
                    type,
                    $"routed Adapter '{GetTypeName(adapterType)}' has no valid RpcCodecAdapterRegistration",
                    type.Locations.FirstOrDefault());
                _failed.Add(GetTypeName(type));
                return true;
            }

            _manifestScopedSelections.Add(type);
            return true;
        }

        private void AddAdapterModel(ITypeSymbol type, string typeName, AdapterRegistration adapter)
        {
            _models[typeName] = new GeneratedCodecModel(
                typeName,
                GetCodecName(typeName),
                GetSchemaId(typeName, adapter.WireFormatId),
                GeneratedCodecKind.Adapter,
                type.IsReferenceType,
                ImmutableArray<GeneratedMemberModel>.Empty,
                ImmutableArray<string>.Empty,
                null,
                null,
                null,
                GetTypeName(adapter.AdapterType),
                adapter.AdapterId,
                adapter.WireFormatId,
                GetAssemblyDependencies([type]),
                type.Locations.FirstOrDefault(),
                IsManifestScoped: _manifestScopedSelections.Remove(type));
        }

        private bool IsRouteEligible(ITypeSymbol type)
        {
            if (_assemblyRoutes.Count == 0 && _conflictingRouteScopes.Count == 0)
                return false;

            if (_routeEligibleTypes is null)
            {
                var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
                CollectCurrentAssemblyRoots(_compilation.Assembly.GlobalNamespace, roots);
                var eligible = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                foreach (var root in roots.Values)
                    CollectRouteEligibleTypes(root, eligible, 0);
                _routeEligibleTypes = eligible;
            }

            return _routeEligibleTypes.Contains(type);
        }

        private void CollectRouteEligibleTypes(
            ITypeSymbol type,
            HashSet<ITypeSymbol> eligible,
            int depth)
        {
            if (depth > MaximumDepth || !eligible.Add(type))
                return;

            if (type is IArrayTypeSymbol array)
            {
                CollectRouteEligibleTypes(array.ElementType, eligible, depth + 1);
                return;
            }

            if (TryGetCollection(type, out _, out var elementType, out var keyType, out var valueType))
            {
                if (elementType is not null)
                    CollectRouteEligibleTypes(elementType, eligible, depth + 1);
                if (keyType is not null)
                    CollectRouteEligibleTypes(keyType, eligible, depth + 1);
                if (valueType is not null)
                    CollectRouteEligibleTypes(valueType, eligible, depth + 1);
                return;
            }

            if (type is not INamedTypeSymbol named || IsThirdPartyType(type))
                return;

            foreach (var member in GetSerializableMembers(named))
                CollectRouteEligibleTypes(GetMemberType(member), eligible, depth + 1);
        }

        private int ClassifyCodecScope(ITypeSymbol type)
        {
            if (IsNativeCodecType(type))
                return RpcCodecScopeNative;
            return type.IsUnmanagedType ? RpcCodecScopeUnmanaged : RpcCodecScopeManaged;
        }

        private bool IsNativeCodecType(ITypeSymbol type)
        {
            if (IsNonOverridableBuiltin(type))
                return true;
            if (TryGetCollection(type, out _, out _, out _, out _))
                return true;
            if (type.IsUnmanagedType || IsThirdPartyType(type))
                return false;

            return CanGenerateNativeDto(type, [], 0);
        }

        private bool CanGenerateNativeDto(ITypeSymbol type, List<ITypeSymbol> stack, int depth)
        {
            if (depth > MaximumDepth ||
                type is not INamedTypeSymbol named ||
                named.IsRefLikeType ||
                !IsAccessibleFromGeneratedCode(named) ||
                named.TypeKind is not (TypeKind.Class or TypeKind.Struct) ||
                named.IsAbstract ||
                HasTypeParameter(named) ||
                named.SpecialType == SpecialType.System_Object ||
                named.TypeKind == TypeKind.Delegate ||
                (named.TypeKind == TypeKind.Class && !named.IsSealed) ||
                named.BaseType is { SpecialType: not SpecialType.System_Object and not SpecialType.System_ValueType } ||
                stack.Any(existing => SymbolEqualityComparer.Default.Equals(existing, type)))
            {
                return false;
            }

            var memberSymbols = GetSerializableMembers(named);
            var memberIds = new HashSet<uint>();
            var analyzedMembers = new List<AnalyzedMember>(memberSymbols.Count);
            stack.Add(type);
            foreach (var member in memberSymbols)
            {
                var memberType = GetMemberType(member);
                var fieldId = GetMemberId(member, out var validId, out var hasExplicitId);
                if (!validId || !memberIds.Add(fieldId))
                {
                    stack.RemoveAt(stack.Count - 1);
                    return false;
                }

                var kind = GetMemberKind(memberType, out var fixedType, out var fixedSize);
                if (kind == GeneratedMemberKind.Complex &&
                    !CanResolveDefaultCodec(memberType, stack, depth + 1))
                {
                    stack.RemoveAt(stack.Count - 1);
                    return false;
                }

                analyzedMembers.Add(new AnalyzedMember(
                    member,
                    memberType,
                    fieldId,
                    kind,
                    fixedType,
                    fixedSize,
                    IsRequired(member),
                    IsNullable(member, memberType),
                    IsNonNullableReference(member, memberType),
                    IsAssignable(member),
                    hasExplicitId,
                    GetEnumUnderlyingType(memberType)));
            }
            stack.RemoveAt(stack.Count - 1);

            return TrySelectConstructor(named, analyzedMembers, out _);
        }

        private bool CanResolveDefaultCodec(ITypeSymbol type, List<ITypeSymbol> stack, int depth)
        {
            if (depth > MaximumDepth ||
                type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
            {
                return false;
            }
            if (IsNonOverridableBuiltin(type) || type.IsUnmanagedType)
                return true;
            if (stack.Any(existing => SymbolEqualityComparer.Default.Equals(existing, type)))
                return false;

            if (TryGetCollection(type, out _, out var elementType, out var keyType, out var valueType))
            {
                stack.Add(type);
                var valid =
                    (elementType is null || CanResolveDefaultCodec(elementType, stack, depth + 1)) &&
                    (keyType is null || CanResolveDefaultCodec(keyType, stack, depth + 1)) &&
                    (valueType is null || CanResolveDefaultCodec(valueType, stack, depth + 1));
                stack.RemoveAt(stack.Count - 1);
                return valid;
            }

            if (IsThirdPartyType(type))
                return false;
            return CanGenerateNativeDto(type, stack, depth);
        }
    }
}
