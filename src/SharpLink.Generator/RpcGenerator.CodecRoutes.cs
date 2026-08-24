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

        private bool TrySelectContractCodecOverride(ITypeSymbol type, out AdapterRegistration? selected)
        {
            if (_selectorOnlyContractDefaults)
                return TrySelectSelectorAdapter(type, out selected);

            // Contract compilation has one deterministic precedence entrypoint:
            // explicit per-type Codec/Adapter selection > assembly route > SharpLink default generation.
            if (TrySelectAdapter(type, out selected))
                return true;
            return TrySelectRouteAdapter(type, out selected);
        }

        private bool TrySelectSelectorAdapter(ITypeSymbol type, out AdapterRegistration? selected)
        {
            selected = null;
            foreach (var attribute in type.GetAttributes())
            {
                if (attribute.AttributeClass is not { } attributeClass ||
                    !_adaptersBySelector.TryGetValue(attributeClass, out var candidate))
                {
                    continue;
                }

                if (selected is null)
                {
                    selected = candidate;
                    continue;
                }

                if (AdapterRegistrationsEqual(selected, candidate))
                    continue;

                // The full Contract-policy pass reports the conflict. The selector-only default pass
                // only needs to avoid publishing an arbitrary default binding.
                selected = null;
                _failed.Add(GetTypeName(type));
                return true;
            }

            return selected is not null;
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

            return true;
        }

        private void AddAdapterModel(ITypeSymbol type, string typeName, AdapterRegistration adapter)
        {
            var schema = adapter.IsDirectCodec
                ? $"direct|{GetTypeName(adapter.AdapterType)}|{adapter.WireFormatId}"
                : adapter.WireFormatId;
            _models[typeName] = new GeneratedCodecModel(
                typeName,
                GetCodecName(typeName, _contractMode),
                GetSchemaId(typeName, schema),
                adapter.IsDirectCodec ? GeneratedCodecKind.Direct : GeneratedCodecKind.Adapter,
                type.IsReferenceType,
                ImmutableArray<GeneratedMemberModel>.Empty,
                ImmutableArray<string>.Empty,
                null,
                null,
                null,
                GetTypeName(adapter.AdapterType),
                adapter.AdapterId,
                adapter.WireFormatId,
                GetAssemblyDependencies([type, adapter.AdapterType]),
                type.Locations.FirstOrDefault());
        }

        private bool IsRouteEligible(ITypeSymbol type)
        {
            if (_assemblyRoutes.Count == 0 && _conflictingRouteScopes.Count == 0)
                return false;

            if (_routeEligibleTypes is null)
            {
                var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
                CollectCurrentContractRouteRoots(_compilation.Assembly.GlobalNamespace, roots);
                var eligible = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                foreach (var root in roots.Values)
                    CollectRouteEligibleTypes(root, eligible, 0);
                _routeEligibleTypes = eligible;
            }

            return _routeEligibleTypes.Contains(type);
        }

        private void CollectCurrentContractRouteRoots(
            INamespaceSymbol namespaceSymbol,
            Dictionary<string, ITypeSymbol> roots)
        {
            foreach (var type in namespaceSymbol.GetTypeMembers())
                CollectCurrentContractRouteRoots(type, roots);
            foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
                CollectCurrentContractRouteRoots(nestedNamespace, roots);
        }

        private void CollectCurrentContractRouteRoots(
            INamedTypeSymbol type,
            Dictionary<string, ITypeSymbol> roots)
        {
            if (type.TypeKind == TypeKind.Interface && HasRpcContractAttribute(type))
                CollectContractPayloadRoots(type, roots);
            foreach (var nested in type.GetTypeMembers())
                CollectCurrentContractRouteRoots(nested, roots);
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
                return CanGenerateNativeCollection(type, [], 0, type);
            if (type.IsUnmanagedType || IsThirdPartyType(type))
                return false;

            return CanGenerateNativeDto(type, [], 0, type);
        }

        private bool CanGenerateNativeCollection(
            ITypeSymbol type,
            List<ITypeSymbol> stack,
            int depth,
            ITypeSymbol blockedRouteType)
        {
            if (depth > MaximumDepth ||
                stack.Any(existing => SymbolEqualityComparer.Default.Equals(existing, type)) ||
                !TryGetCollection(type, out _, out var elementType, out var keyType, out var valueType))
            {
                return false;
            }

            stack.Add(type);
            var valid =
                (elementType is null || CanResolveContractCodecDependency(elementType, stack, depth + 1, blockedRouteType)) &&
                (keyType is null || CanResolveContractCodecDependency(keyType, stack, depth + 1, blockedRouteType)) &&
                (valueType is null || CanResolveContractCodecDependency(valueType, stack, depth + 1, blockedRouteType));
            stack.RemoveAt(stack.Count - 1);
            return valid;
        }

        private bool CanGenerateNativeDto(
            ITypeSymbol type,
            List<ITypeSymbol> stack,
            int depth,
            ITypeSymbol blockedRouteType)
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
                    !CanResolveContractCodecDependency(memberType, stack, depth + 1, blockedRouteType))
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

        private bool CanResolveContractCodecDependency(
            ITypeSymbol type,
            List<ITypeSymbol> stack,
            int depth,
            ITypeSymbol blockedRouteType)
        {
            if (depth > MaximumDepth ||
                type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
            {
                return false;
            }

            // Explicit per-type Adapter/direct Codec bindings are part of the Contract policy graph
            // and can make an outer generated DTO/collection a valid Native shell.
            if (HasResolvableExplicitAdapter(type))
                return true;
            if (IsNonOverridableBuiltin(type) || type.IsUnmanagedType)
                return true;
            if (stack.Any(existing => SymbolEqualityComparer.Default.Equals(existing, type)))
                return false;

            if (TryGetCollection(type, out _, out _, out _, out _) &&
                CanGenerateNativeCollection(type, stack, depth, blockedRouteType))
            {
                return true;
            }
            if (!IsThirdPartyType(type) &&
                CanGenerateNativeDto(type, stack, depth, blockedRouteType))
            {
                return true;
            }

            // A nested non-Native dependency may itself be satisfied by the Contract route. Never
            // use the root's own route to prove that the root is Native; that would be circular.
            if (SymbolEqualityComparer.Default.Equals(type, blockedRouteType) || !IsRouteEligible(type))
                return false;
            var scope = type.IsUnmanagedType ? RpcCodecScopeUnmanaged : RpcCodecScopeManaged;
            return !_conflictingRouteScopes.Contains(scope) &&
                   _assemblyRoutes.TryGetValue(scope, out var adapterType) &&
                   _adaptersByType.ContainsKey(adapterType);
        }
    }
}
