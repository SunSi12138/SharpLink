namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        internal DtoAnalysisPassResult AnalyzeDtoCandidates()
        {
            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(
                _compilation.Assembly.GlobalNamespace,
                roots,
                includeSerializable: !_contractMode,
                includeContracts: _contractMode);
            foreach (var root in roots.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Visit(root.Value, [], 0);
            }

            return new DtoAnalysisPassResult(
                _models.Values.OrderBy(static model => model.TypeName, StringComparer.Ordinal).ToImmutableArray(),
                _diagnostics.ToImmutableArray(),
                _enums.Values.OrderBy(static item => item.TypeName, StringComparer.Ordinal).ToImmutableArray());
        }

        private void CollectCurrentAssemblyRoots(
            INamespaceSymbol namespaceSymbol,
            Dictionary<string, ITypeSymbol> roots,
            bool includeSerializable,
            bool includeContracts)
        {
            foreach (var type in namespaceSymbol.GetTypeMembers())
                CollectCurrentAssemblyRoots(type, roots, includeSerializable, includeContracts);
            foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
                CollectCurrentAssemblyRoots(nestedNamespace, roots, includeSerializable, includeContracts);
        }

        private void CollectCurrentAssemblyRoots(
            INamedTypeSymbol type,
            Dictionary<string, ITypeSymbol> roots,
            bool includeSerializable,
            bool includeContracts)
        {
            if (includeSerializable && HasAttribute(type, "SharpLink.Sdk", "RpcSerializableAttribute"))
                AddRoot(roots, type);
            if (includeContracts && type.TypeKind == TypeKind.Interface && HasRpcContractAttribute(type))
                CollectContractPayloadRoots(type, roots);

            foreach (var nested in type.GetTypeMembers())
                CollectCurrentAssemblyRoots(nested, roots, includeSerializable, includeContracts);
        }

        private void CollectReferencedContractRoots(Dictionary<string, ITypeSymbol> roots)
        {
            foreach (var reference in _compilation.References)
            {
                if (_compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly ||
                    !_allowedAssemblyNames.Contains(assembly.Identity.Name) ||
                    HasGeneratedAssemblyManifest(assembly))
                {
                    continue;
                }

                CollectReferencedContractRoots(assembly.GlobalNamespace, roots);
            }
        }

        private void CollectReferencedContractRoots(
            INamespaceSymbol namespaceSymbol,
            Dictionary<string, ITypeSymbol> roots)
        {
            foreach (var type in namespaceSymbol.GetTypeMembers())
                CollectReferencedContractRoots(type, roots, containingTypesArePublic: true);
            foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
                CollectReferencedContractRoots(nestedNamespace, roots);
        }

        private void CollectReferencedContractRoots(
            INamedTypeSymbol type,
            Dictionary<string, ITypeSymbol> roots,
            bool containingTypesArePublic)
        {
            var publiclyReachable = containingTypesArePublic && type.DeclaredAccessibility == Accessibility.Public;
            if (!publiclyReachable)
                return;
            if (type.TypeKind == TypeKind.Interface && HasRpcContractAttribute(type))
                CollectContractPayloadRoots(type, roots);
            foreach (var nested in type.GetTypeMembers())
                CollectReferencedContractRoots(nested, roots, publiclyReachable);
        }

        private static void CollectContractPayloadRoots(
            INamedTypeSymbol contract,
            Dictionary<string, ITypeSymbol> roots)
        {
            foreach (var method in GetContractMethods(contract))
            {
                foreach (var parameter in method.Parameters)
                {
                    if (IsCancellationTokenParameter(parameter))
                        continue;
                    if (IsAsyncEnumerable(parameter.Type, out var streamItem))
                        AddRoot(roots, streamItem!);
                    else
                        AddRoot(roots, parameter.Type);
                }

                if (IsAsyncEnumerable(method.ReturnType, out var returnStreamItem))
                {
                    AddRoot(roots, returnStreamItem!);
                }
                else if (method.ReturnType is INamedTypeSymbol { IsGenericType: true } taskLike &&
                         taskLike.TypeArguments.Length == 1)
                {
                    AddRoot(roots, taskLike.TypeArguments[0]);
                }
            }
        }

        private static void AddRoot(Dictionary<string, ITypeSymbol> roots, ITypeSymbol type)
        {
            var key = GetTypeName(type);
            if (!roots.ContainsKey(key))
                roots.Add(key, type);
        }

        private void Visit(ITypeSymbol type, List<ITypeSymbol> stack, int depth)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            CollectEnums(type);
            var typeName = GetTypeName(type);
            if (_models.ContainsKey(typeName) || _failed.Contains(typeName))
                return;
            if (type is INamedTypeSymbol namedArtifact)
            {
                if (namedArtifact.IsRefLikeType)
                {
                    Report(DtoDiagnosticKind.Unsupported, type,
                        "ref-like DTOs cannot be used by generated Codec or RPC artifacts");
                    _failed.Add(typeName);
                    return;
                }
                if (!IsAccessibleFromGeneratedCode(namedArtifact))
                {
                    Report(DtoDiagnosticKind.Unsupported, type,
                        "the DTO type and every containing type must be accessible from generated code");
                    _failed.Add(typeName);
                    return;
                }
            }
            if (type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
            {
                Report(DtoDiagnosticKind.Unsupported, type,
                    "pointer and function-pointer values cannot be represented by generated Codec or RPC artifacts");
                _failed.Add(typeName);
                return;
            }

            // Policy declarations are candidates only at this stage. Final custom/adapter selection,
            // validation and factory materialization happen in ResolveFinalCodecPlan so emitted
            // behavior and CodecHash consume the same resolved node.
            if (HasCodecPolicyCandidate(type))
                return;

            if (type.TypeKind == TypeKind.Dynamic)
            {
                Report(DtoDiagnosticKind.Unsupported, type,
                    "dynamic values cannot be represented by generated Codec or RPC artifacts; use a concrete closed payload type");
                _failed.Add(typeName);
                return;
            }

            if (HasRuntimeCodecWithoutGeneratedFactoryCandidate(type) &&
                !HasCompositeCodecPolicyCandidate(type))
            {
                return;
            }
            if (depth > MaximumDepth)
            {
                Report(DtoDiagnosticKind.Depth, type, $"more than {MaximumDepth} nested types");
                _failed.Add(typeName);
                return;
            }
            if (type.SpecialType == SpecialType.System_Object ||
                type.TypeKind is TypeKind.Delegate or TypeKind.Dynamic)
            {
                Report(DtoDiagnosticKind.Unsupported, type, "object, delegate, dynamic, pointer, and function-pointer values require an explicit typed Codec");
                _failed.Add(typeName);
                return;
            }
            if (stack.Any(existing => SymbolEqualityComparer.Default.Equals(existing, type)))
            {
                var path = string.Join(" -> ", stack.Select(GetTypeName).Concat([typeName]));
                Report(DtoDiagnosticKind.Cycle, type, path);
                foreach (var item in stack)
                    _failed.Add(GetTypeName(item));
                _failed.Add(typeName);
                return;
            }

            if (TryGetCollection(type, out var collectionKind, out var elementType, out var keyType, out var valueType))
            {
                stack.Add(type);
                if (elementType is not null)
                    Visit(elementType, stack, depth + 1);
                if (keyType is not null)
                    Visit(keyType, stack, depth + 1);
                if (valueType is not null)
                    Visit(valueType, stack, depth + 1);
                stack.RemoveAt(stack.Count - 1);
                if (_failed.Contains(typeName))
                    return;

                _models[typeName] = new GeneratedCodecModel(
                    typeName,
                    GetCodecName(typeName, _contractMode),
                    GetSchemaId(typeName, collectionKind.ToString()),
                    collectionKind,
                    type.IsReferenceType,
                    ImmutableArray<GeneratedMemberModel>.Empty,
                    ImmutableArray<string>.Empty,
                    elementType is null ? null : GetTypeName(elementType),
                    keyType is null ? null : GetTypeName(keyType),
                    valueType is null ? null : GetTypeName(valueType),
                    null,
                    null,
                    null,
                    "sharplink-native/v1",
                    GetAssemblyDependencies([type]),
                    type.Locations.FirstOrDefault())
                {
                    ElementIsString = elementType?.SpecialType == SpecialType.System_String
                };
                return;
            }

            // Referenced generated Codec metadata is only a discovery candidate here.
            // Its hash and ABI provenance are validated later by ResolveFinalCodecPlan.
            if (HasReferencedGeneratedCodecIdentityCandidate(type))
                return;

            if (IsThirdPartyType(type))
            {
                Report(DtoDiagnosticKind.Unsupported, type,
                    "the type is owned by a referenced assembly and has no registered Codec Adapter or custom RpcCodec binding; add a serializer selector Attribute, an assembly-level [RpcCodecAdapter(typeof(Target), typeof(Adapter))], or [RpcCodec(typeof(Target), typeof(Codec))] binding",
                    type.Locations.FirstOrDefault());
                _failed.Add(typeName);
                return;
            }

            AnalyzeDto(type, stack, depth);
        }

        private ImmutableArray<string> GetAssemblyDependencies(IEnumerable<ITypeSymbol> types)
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in types)
                CollectAssemblyDependencies(type, identities);
            return identities.OrderBy(static identity => identity, StringComparer.Ordinal).ToImmutableArray();
        }

        private void CollectAssemblyDependencies(ITypeSymbol type, HashSet<string> identities)
        {
            if (type is IArrayTypeSymbol array)
            {
                CollectAssemblyDependencies(array.ElementType, identities);
                return;
            }
            if (type is not INamedTypeSymbol named)
                return;

            var assembly = named.ContainingAssembly;
            if (assembly is not null &&
                !SymbolEqualityComparer.Default.Equals(assembly, _compilation.Assembly) &&
                HasGeneratedAssemblyManifest(assembly))
            {
                identities.Add(assembly.Identity.ToString());
            }
            foreach (var argument in named.TypeArguments)
                CollectAssemblyDependencies(argument, identities);
        }

        private void CollectEnums(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array)
            {
                CollectEnums(array.ElementType);
                return;
            }
            if (type is not INamedTypeSymbol named)
                return;
            if (named.TypeKind == TypeKind.Enum && named.EnumUnderlyingType is { } underlying)
            {
                var typeName = GetTypeName(named);
                if (!_enums.ContainsKey(typeName))
                {
                    _enums.Add(typeName, new GeneratedEnumModel(
                        typeName,
                        GetTypeName(underlying),
                        named.Locations.FirstOrDefault()));
                }
                return;
            }
            foreach (var argument in named.TypeArguments)
                CollectEnums(argument);
        }

        private static bool HasReferencedGeneratedCodecIdentityCandidate(ITypeSymbol type)
        {
            var assembly = type.ContainingAssembly;
            if (assembly is null)
                return false;

            foreach (var attribute in assembly.GetAttributes())
            {
                if (IsAttribute(attribute, "SharpLink.Abstractions", "SharpLinkGeneratedCodecIdentityAttribute") &&
                    attribute.ConstructorArguments.Length == 3 &&
                    attribute.ConstructorArguments[0].Value is ITypeSymbol targetType &&
                    SymbolEqualityComparer.Default.Equals(targetType, type))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsThirdPartyType(ITypeSymbol type)
            => type.ContainingAssembly is { } assembly && !_allowedAssemblyNames.Contains(assembly.Identity.Name);

        private static bool TryGetCollection(
            ITypeSymbol type,
            out GeneratedCodecKind kind,
            out ITypeSymbol? elementType,
            out ITypeSymbol? keyType,
            out ITypeSymbol? valueType)
        {
            elementType = null;
            keyType = null;
            valueType = null;
            if (type is IArrayTypeSymbol array)
            {
                kind = GeneratedCodecKind.Array;
                if (array.Rank == 1)
                {
                    elementType = array.ElementType;
                    return true;
                }
                return false;
            }
            if (type is not INamedTypeSymbol named || !named.IsGenericType)
            {
                kind = default;
                return false;
            }

            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                kind = GeneratedCodecKind.Nullable;
                elementType = named.TypeArguments[0];
                return true;
            }

            var definition = named.OriginalDefinition.ToDisplayString();
            switch (definition)
            {
                case "System.Collections.Generic.List<T>":
                    kind = GeneratedCodecKind.List;
                    elementType = named.TypeArguments[0];
                    return true;
                case "System.Collections.Generic.Dictionary<TKey, TValue>":
                    kind = GeneratedCodecKind.Dictionary;
                    keyType = named.TypeArguments[0];
                    valueType = named.TypeArguments[1];
                    return true;
                case "System.Memory<T>":
                    kind = GeneratedCodecKind.Memory;
                    elementType = named.TypeArguments[0];
                    return true;
                case "System.ReadOnlyMemory<T>":
                    kind = GeneratedCodecKind.ReadOnlyMemory;
                    elementType = named.TypeArguments[0];
                    return true;
                case "System.Collections.Immutable.ImmutableArray<T>":
                    kind = GeneratedCodecKind.ImmutableArray;
                    elementType = named.TypeArguments[0];
                    return true;
                default:
                    kind = default;
                    return false;
            }
        }

        private static bool HasRuntimeCodecWithoutGeneratedFactoryCandidate(ITypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_String || GetFixedSize(type) != 0 || type.IsUnmanagedType)
                return true;
            if (type is INamedTypeSymbol nullable &&
                nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                GetFixedSize(nullable.TypeArguments[0]) != 0)
            {
                return true;
            }
            if (!TryGetCollection(type, out var kind, out var element, out _, out _) ||
                kind is GeneratedCodecKind.Dictionary or GeneratedCodecKind.Nullable ||
                element is null || element.TypeKind == TypeKind.Enum)
            {
                return false;
            }

            return global::SharpLink.RpcBuiltinCollectionWireCatalog.TryGet(
                element.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                out _);
        }

        // Kept as a compatibility alias for pre-plan candidate utilities. New discovery and final
        // selection code should use the explicit runtime-factory wording above.
        private static bool IsBuiltin(ITypeSymbol type)
            => HasRuntimeCodecWithoutGeneratedFactoryCandidate(type);

        private static ITypeSymbol NormalizeAdapterTarget(ITypeSymbol type)
            => type is INamedTypeSymbol
            {
                IsTupleType: true,
                TupleUnderlyingType: { } underlying
            }
                ? underlying
                : type;
    }
}
