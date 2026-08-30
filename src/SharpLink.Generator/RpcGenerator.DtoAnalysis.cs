namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static IEnumerable<string> GetCodecDependencies(GeneratedCodecModel codec)
    {
        if (codec.ElementType is not null)
            yield return codec.ElementType;
        if (codec.KeyType is not null)
            yield return codec.KeyType;
        if (codec.ValueType is not null)
            yield return codec.ValueType;
        foreach (var member in codec.Members)
        {
            if (member.Kind == GeneratedMemberKind.Complex)
                yield return member.TypeName;
        }
    }

    private static bool HasSameCodecDefinition(GeneratedCodecModel left, GeneratedCodecModel right)
    {
        if (!string.Equals(left.TypeName, right.TypeName, StringComparison.Ordinal) ||
            !string.Equals(left.SchemaId, right.SchemaId, StringComparison.Ordinal) ||
            left.Kind != right.Kind || left.IsReferenceType != right.IsReferenceType ||
            !string.Equals(left.ElementType, right.ElementType, StringComparison.Ordinal) ||
            !string.Equals(left.KeyType, right.KeyType, StringComparison.Ordinal) ||
            !string.Equals(left.ValueType, right.ValueType, StringComparison.Ordinal) ||
            !string.Equals(left.AdapterType, right.AdapterType, StringComparison.Ordinal) ||
            !string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal) ||
            !string.Equals(left.WireFormatId, right.WireFormatId, StringComparison.Ordinal) ||
            !left.ConstructorMembers.SequenceEqual(right.ConstructorMembers, StringComparer.Ordinal) ||
            !left.AssemblyDependencies.SequenceEqual(right.AssemblyDependencies, StringComparer.Ordinal) ||
            left.Members.Length != right.Members.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Members.Length; index++)
        {
            if (left.Members[index] with { Location = null } != right.Members[index] with { Location = null })
                return false;
        }
        return true;
    }

    private sealed record DtoAnalysisPassResult(
        ImmutableArray<GeneratedCodecModel> Codecs,
        ImmutableArray<DtoDiagnosticModel> Diagnostics,
        ImmutableArray<GeneratedEnumModel> Enums);

    private sealed partial class DtoAnalysisState
    {
        private const int MaximumDepth = 64;
        private readonly Compilation _compilation;
        private readonly CancellationToken _cancellationToken;
        private readonly bool _contractMode;
        private readonly bool _applyCodecPolicy;
        private readonly HashSet<string> _allowedAssemblyNames;
        private readonly Dictionary<ITypeSymbol, AdapterRegistration> _adaptersByType =
            new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ITypeSymbol, AdapterRegistration> _adaptersBySelector =
            new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ITypeSymbol, ExplicitBindingCandidate> _assemblyBindings =
            new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ITypeSymbol, CustomCodecRegistration> _customCodecBindings =
            new(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, GeneratedCodecModel> _models = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GeneratedEnumModel> _enums = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);
        private readonly List<DtoDiagnosticModel> _diagnostics = [];

        public DtoAnalysisState(
            Compilation compilation,
            CancellationToken cancellationToken,
            bool contractMode,
            bool applyCodecPolicy)
            : this(
                compilation,
                cancellationToken,
                contractMode,
                applyCodecPolicy,
                selectorOnlyContractDefault: false)
        {
        }

        public DtoAnalysisPassResult Analyze()
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

        private void CollectAdapterRegistrations()
        {
            var assemblies = new Dictionary<string, IAssemblySymbol>(StringComparer.Ordinal)
            {
                [_compilation.Assembly.Identity.ToString()] = _compilation.Assembly
            };
            var pending = new Queue<IAssemblySymbol>();
            pending.Enqueue(_compilation.Assembly);
            while (pending.Count != 0)
            {
                var assembly = pending.Dequeue();
                foreach (var referenced in assembly.Modules.SelectMany(static module => module.ReferencedAssemblySymbols)
                             .OrderBy(static item => item.Identity.ToString(), StringComparer.Ordinal))
                {
                    if (!assemblies.ContainsKey(referenced.Identity.ToString()))
                    {
                        assemblies.Add(referenced.Identity.ToString(), referenced);
                        pending.Enqueue(referenced);
                    }
                }
            }

            var adapterIds = new Dictionary<string, AdapterRegistration>(StringComparer.Ordinal);
            foreach (var assembly in assemblies.Values.OrderBy(static item => item.Identity.ToString(), StringComparer.Ordinal))
            {
                foreach (var attribute in assembly.GetAttributes()
                             .Where(static attribute => IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAdapterRegistrationAttribute"))
                             .OrderBy(static attribute => attribute.ToString(), StringComparer.Ordinal))
                {
                    var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? Location.None;
                    if (attribute.ConstructorArguments.Length != 3 ||
                        attribute.ConstructorArguments[0].Value is not INamedTypeSymbol adapterType ||
                        attribute.ConstructorArguments[1].Value is not string adapterId ||
                        attribute.ConstructorArguments[2].Value is not string wireFormatId ||
                        !IsStableIdentity(adapterId) || !IsStableIdentity(wireFormatId))
                    {
                        Report(DtoDiagnosticKind.AdapterRegistrationInvalid, assembly,
                            "registration requires a concrete Adapter type and non-empty stable ASCII Adapter/Wire Format IDs", location);
                        continue;
                    }

                    ITypeSymbol? selector = null;
                    foreach (var namedArgument in attribute.NamedArguments)
                    {
                        if (namedArgument.Key == "SelectorAttributeType")
                            selector = namedArgument.Value.Value as ITypeSymbol;
                    }
                    if (!IsValidAdapterType(adapterType))
                    {
                        Report(DtoDiagnosticKind.AdapterTypeInvalid, adapterType,
                            "Adapter must implement IRpcCodecAdapter, be public sealed, and expose a public parameterless constructor", location);
                        continue;
                    }
                    if (selector is not null && !InheritsFromAttribute(selector))
                    {
                        Report(DtoDiagnosticKind.AdapterRegistrationInvalid, selector,
                            "SelectorAttributeType must derive from System.Attribute", location);
                        continue;
                    }

                    var registration = new AdapterRegistration(
                        adapterType,
                        adapterId,
                        wireFormatId,
                        selector,
                        location);
                    if (_adaptersByType.TryGetValue(adapterType, out var existingType) &&
                        (!string.Equals(existingType.AdapterId, adapterId, StringComparison.Ordinal) ||
                         !string.Equals(existingType.WireFormatId, wireFormatId, StringComparison.Ordinal)))
                    {
                        Report(DtoDiagnosticKind.AdapterIdentityConflict, adapterType,
                            "the same Adapter type has inconsistent Adapter or Wire Format IDs", location);
                        continue;
                    }
                    if (adapterIds.TryGetValue(adapterId, out var existingId) &&
                        (!SymbolEqualityComparer.Default.Equals(existingId.AdapterType, adapterType) ||
                         !string.Equals(existingId.WireFormatId, wireFormatId, StringComparison.Ordinal)))
                    {
                        Report(DtoDiagnosticKind.AdapterIdentityConflict, adapterType,
                            $"Adapter ID '{adapterId}' is declared by inconsistent types or Wire Format IDs", location);
                        continue;
                    }
                    if (selector is not null && _adaptersBySelector.TryGetValue(selector, out var existingSelector) &&
                        !SymbolEqualityComparer.Default.Equals(existingSelector.AdapterType, adapterType))
                    {
                        Report(DtoDiagnosticKind.SelectorConflict, selector,
                            "one selector Attribute cannot select multiple Codec Adapters", location);
                        continue;
                    }

                    _adaptersByType[adapterType] = registration;
                    adapterIds[adapterId] = registration;
                    if (selector is not null)
                        _adaptersBySelector[selector] = registration;
                }
            }
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
            if (TrySelectCustomCodec(type, out var customCodec))
            {
                if (customCodec is not null)
                {
                    _models[typeName] = new GeneratedCodecModel(
                        typeName,
                        GetCodecName(typeName, _contractMode),
                        GetSchemaId(typeName, customCodec.SchemaId),
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
                        customCodec.WireFormatId,
                        GetAssemblyDependencies([type]),
                        type.Locations.FirstOrDefault());
                }
                return;
            }

            if (type.TypeKind == TypeKind.Dynamic)
            {
                Report(DtoDiagnosticKind.Unsupported, type,
                    "dynamic values cannot be represented by generated Codec or RPC artifacts; use a concrete closed payload type");
                _failed.Add(typeName);
                return;
            }

            AdapterRegistration? selectedAdapter = null;
            var hasSelectedOverride = _applyCodecPolicy &&
                (_contractMode
                    ? TrySelectContractCodecOverride(type, out selectedAdapter)
                    : TrySelectAdapter(type, out selectedAdapter));
            if (hasSelectedOverride)
            {
                if (selectedAdapter is not null)
                    AddAdapterModel(type, typeName, selectedAdapter);
                return;
            }

            if (IsBuiltin(type) && !HasSelectedCompositeCodecDependency(type))
                return;
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

        private void AnalyzeDto(ITypeSymbol type, List<ITypeSymbol> stack, int depth)
        {
            var typeName = GetTypeName(type);
            if (type is not INamedTypeSymbol named)
            {
                Report(DtoDiagnosticKind.Unsupported, type, "only closed, non-abstract class/record/struct DTOs are supported");
                _failed.Add(typeName);
                return;
            }
            if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct) ||
                named.IsAbstract ||
                HasTypeParameter(named) ||
                named.SpecialType == SpecialType.System_Object ||
                named.TypeKind == TypeKind.Delegate)
            {
                Report(DtoDiagnosticKind.Unsupported, type, "only closed, non-abstract class/record/struct DTOs are supported");
                _failed.Add(typeName);
                return;
            }
            if (named.TypeKind == TypeKind.Class && !named.IsSealed)
            {
                Report(DtoDiagnosticKind.Unsupported, type,
                    "classes must be sealed; add an installed serializer selector Attribute or [RpcCodecAdapter(typeof(...))] for polymorphic graphs");
                _failed.Add(typeName);
                return;
            }
            if (named.BaseType is { SpecialType: not SpecialType.System_Object and not SpecialType.System_ValueType })
            {
                Report(DtoDiagnosticKind.Unsupported, type, "DTO inheritance is outside the native Codec subset");
                _failed.Add(typeName);
                return;
            }

            var memberSymbols = GetSerializableMembers(named);
            var memberIds = new Dictionary<uint, string>();
            var analyzedMembers = new List<AnalyzedMember>(memberSymbols.Count);
            stack.Add(type);
            foreach (var member in memberSymbols)
            {
                var memberType = GetMemberType(member);
                CollectEnums(memberType);
                var fieldId = GetMemberId(member, out var validId, out var hasExplicitId);
                if (!validId)
                {
                    Report(DtoDiagnosticKind.Unsupported, type, $"member '{member.Name}' has an invalid RpcMember ID", member.Locations.FirstOrDefault());
                    _failed.Add(typeName);
                    continue;
                }
                if (memberIds.TryGetValue(fieldId, out var existingMember))
                {
                    Report(
                        DtoDiagnosticKind.MemberIdCollision,
                        type,
                        $"{fieldId} is used by '{existingMember}' and '{member.Name}'",
                        member.Locations.FirstOrDefault());
                    _failed.Add(typeName);
                    continue;
                }
                memberIds.Add(fieldId, member.Name);

                var kind = GetMemberKind(memberType, out var fixedType, out var fixedSize);
                if (kind == GeneratedMemberKind.Complex)
                    Visit(memberType, stack, depth + 1);
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
            if (_failed.Contains(typeName))
                return;

            if (!TrySelectConstructor(named, analyzedMembers, out var constructorMembers))
            {
                Report(DtoDiagnosticKind.Constructor, type, "public members cannot be restored by an accessible constructor and object initializer");
                _failed.Add(typeName);
                return;
            }

            var constructorSet = new HashSet<string>(constructorMembers, StringComparer.Ordinal);
            var generatedMembers = analyzedMembers
                .OrderBy(static member => member.FieldId)
                .Select(member => new GeneratedMemberModel(
                    member.Symbol.Name,
                    member.Symbol.Name,
                    GetTypeName(member.Type),
                    member.FieldId,
                    member.Kind,
                    member.FixedType is null ? null : GetTypeName(member.FixedType),
                    member.FixedSize,
                    member.Required,
                    member.Nullable,
                    member.NonNullableReference,
                    constructorSet.Contains(member.Symbol.Name),
                    member.Assignable && (!constructorSet.Contains(member.Symbol.Name) || member.Required),
                    member.HasExplicitId,
                    member.EnumUnderlyingType,
                    member.Symbol.Locations.FirstOrDefault()))
                .ToImmutableArray();

            var schema = new StringBuilder(typeName);
            foreach (var member in generatedMembers)
            {
                schema.Append('|').Append(member.FieldId).Append(':').Append(member.TypeName)
                    .Append(':').Append(member.Required);
                if (member.Nullable)
                    schema.Append(":nullable");
            }
            var dependencyTypes = new List<ITypeSymbol>(analyzedMembers.Count + 1) { type };
            dependencyTypes.AddRange(analyzedMembers.Select(static member => member.Type));
            _models[typeName] = new GeneratedCodecModel(
                typeName,
                GetCodecName(typeName, _contractMode),
                GetSchemaId(typeName, schema.ToString()),
                GeneratedCodecKind.Dto,
                named.IsReferenceType,
                generatedMembers,
                constructorMembers.ToImmutableArray(),
                null,
                null,
                null,
                null,
                null,
                null,
                "sharplink-native/v1",
                GetAssemblyDependencies(dependencyTypes),
                named.Locations.FirstOrDefault());
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

        private List<ISymbol> GetSerializableMembers(INamedTypeSymbol type)
        {
            var members = new List<ISymbol>();
            foreach (var member in type.GetMembers())
            {
                if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public ||
                    HasAttribute(member, "SharpLink.Sdk", "RpcIgnoreAttribute"))
                {
                    continue;
                }
                if (member is IFieldSymbol { IsConst: false } field)
                    members.Add(field);
                else if (member is IPropertySymbol { IsIndexer: false, GetMethod.DeclaredAccessibility: Accessibility.Public } property)
                    members.Add(property);
            }
            return members;
        }

        private bool TrySelectConstructor(
            INamedTypeSymbol type,
            List<AnalyzedMember> members,
            out List<string> constructorMembers)
        {
            if (type.TypeKind == TypeKind.Struct && members.All(static member => member.Assignable))
            {
                constructorMembers = [];
                return CompilerRequiredMembersAreSatisfied(type, members, setsRequiredMembers: false);
            }

            var memberByName = members.ToDictionary(
                static member => member.Symbol.Name,
                StringComparer.Ordinal);
            foreach (var constructor in type.InstanceConstructors
                         .Where(IsConstructorAccessible)
                         .Where(static constructor => constructor.Parameters.All(static parameter =>
                             parameter.RefKind is not (RefKind.Ref or RefKind.Out or RefKind.RefReadOnlyParameter)))
                         .OrderBy(static constructor => constructor.Parameters.Length)
                         .ThenBy(static constructor => constructor.ToDisplayString(), StringComparer.Ordinal))
            {
                var mapped = new List<string>(constructor.Parameters.Length);
                var valid = true;
                foreach (var parameter in constructor.Parameters)
                {
                    if (parameter.Name is null ||
                        !TryGetConstructorMember(parameter.Name, out var member) ||
                        !SymbolEqualityComparer.Default.Equals(parameter.Type, member.Type))
                    {
                        valid = false;
                        break;
                    }
                    mapped.Add(member.Symbol.Name);
                }
                if (!valid)
                    continue;
                var mappedSet = new HashSet<string>(mapped, StringComparer.Ordinal);
                if (members.Any(member => !member.Assignable && !mappedSet.Contains(member.Symbol.Name)))
                    continue;
                if (!CompilerRequiredMembersAreSatisfied(
                        type,
                        members,
                        HasAttribute(
                            constructor,
                            "System.Diagnostics.CodeAnalysis",
                            "SetsRequiredMembersAttribute")))
                {
                    continue;
                }
                constructorMembers = mapped;
                return true;
            }

            constructorMembers = [];
            return false;

            bool TryGetConstructorMember(string parameterName, out AnalyzedMember member)
            {
                if (memberByName.TryGetValue(parameterName, out member!))
                    return true;

                AnalyzedMember? candidate = null;
                foreach (var current in members)
                {
                    if (!string.Equals(current.Symbol.Name, parameterName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (candidate is not null)
                    {
                        member = null!;
                        return false;
                    }
                    candidate = current;
                }
                member = candidate!;
                return candidate is not null;
            }
        }

        private static bool CompilerRequiredMembersAreSatisfied(
            INamedTypeSymbol type,
            List<AnalyzedMember> members,
            bool setsRequiredMembers)
        {
            if (setsRequiredMembers)
                return true;

            var serializedMembers = new HashSet<ISymbol>(
                members.Where(static member => member.Assignable).Select(static member => member.Symbol),
                SymbolEqualityComparer.Default);
            return type.GetMembers()
                .Where(IsCompilerRequired)
                .All(serializedMembers.Contains);
        }

        private bool IsConstructorAccessible(IMethodSymbol constructor)
        {
            if (constructor.DeclaredAccessibility == Accessibility.Public)
                return true;
            if (!SymbolEqualityComparer.Default.Equals(constructor.ContainingAssembly, _compilation.Assembly))
                return false;
            return constructor.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal;
        }

        private bool TrySelectAdapter(ITypeSymbol type, out AdapterRegistration? selected)
        {
            if (!TryCollectExplicitAdapterCandidates(type, reportInvalid: true, out var candidates))
            {
                selected = null;
                _failed.Add(GetTypeName(type));
                return true;
            }
            if (candidates.Count == 0)
            {
                selected = null;
                return false;
            }

            var resolved = new List<AdapterRegistration>();
            foreach (var candidate in candidates)
            {
                if (!TryResolveExplicitBinding(type, candidate, reportInvalid: true, out var registration))
                {
                    selected = null;
                    _failed.Add(GetTypeName(type));
                    return true;
                }
                if (registration is not null && !resolved.Any(existing => AdapterRegistrationsEqual(existing, registration)))
                    resolved.Add(registration);
            }

            if (resolved.Count != 1)
            {
                Report(DtoDiagnosticKind.AdapterSelectionConflict, type,
                    "the target selects multiple different explicit Codec Adapters", candidates[0].Location);
                selected = null;
                _failed.Add(GetTypeName(type));
                return true;
            }
            selected = resolved[0];
            return true;
        }

        private bool HasResolvableExplicitAdapter(ITypeSymbol type)
        {
            if (!TryCollectExplicitAdapterCandidates(type, reportInvalid: false, out var candidates) ||
                candidates.Count == 0)
            {
                return false;
            }

            var resolved = new List<AdapterRegistration>();
            foreach (var candidate in candidates)
            {
                if (!TryResolveExplicitBinding(type, candidate, reportInvalid: false, out var registration) ||
                    registration is null)
                {
                    return false;
                }
                if (!resolved.Any(existing => AdapterRegistrationsEqual(existing, registration)))
                    resolved.Add(registration);
            }
            return resolved.Count == 1;
        }

        private bool TryCollectExplicitAdapterCandidates(
            ITypeSymbol type,
            bool reportInvalid,
            out List<ExplicitBindingCandidate> candidates)
        {
            candidates = [];
            foreach (var attribute in type.GetAttributes())
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ??
                    type.Locations.FirstOrDefault() ?? Location.None;
                if (IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAdapterAttribute"))
                {
                    if (attribute.ConstructorArguments.Length != 1 ||
                        attribute.ConstructorArguments[0].Value is not INamedTypeSymbol adapter)
                    {
                        if (reportInvalid)
                        {
                            Report(DtoDiagnosticKind.AdapterBindingInvalid, type,
                                "type-level RpcCodecAdapter requires only adapterType", location);
                        }
                        return false;
                    }
                    candidates.Add(new ExplicitBindingCandidate(adapter, location));
                }
                if (attribute.AttributeClass is { } attributeClass &&
                    _adaptersBySelector.TryGetValue(attributeClass, out var selectorRegistration))
                {
                    candidates.Add(new ExplicitBindingCandidate(selectorRegistration.AdapterType, location));
                }
            }
            if (_assemblyBindings.TryGetValue(NormalizeAdapterTarget(type), out var assemblyBinding))
                candidates.Add(assemblyBinding);
            return true;
        }

        private bool TryResolveExplicitBinding(
            ITypeSymbol target,
            ExplicitBindingCandidate candidate,
            bool reportInvalid,
            out AdapterRegistration? selected)
        {
            if (_adaptersByType.TryGetValue(candidate.ImplementationType, out var adapter))
            {
                selected = adapter;
                return true;
            }

            if (reportInvalid)
            {
                var detail = ImplementsRpcCodecAdapter(candidate.ImplementationType)
                    ? $"selected Adapter '{GetTypeName(candidate.ImplementationType)}' has no valid RpcCodecAdapterRegistration"
                    : $"selected RpcCodecAdapter implementation '{GetTypeName(candidate.ImplementationType)}' must implement IRpcCodecAdapter; use RpcCodec for handwritten IRpcCodec<T> bindings";
                Report(
                    DtoDiagnosticKind.AdapterRegistrationInvalid,
                    target,
                    detail,
                    candidate.Location);
            }
            selected = null;
            return false;
        }

        private static bool AdapterRegistrationsEqual(AdapterRegistration left, AdapterRegistration right)
            => SymbolEqualityComparer.Default.Equals(left.AdapterType, right.AdapterType) &&
               string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal) &&
               string.Equals(left.WireFormatId, right.WireFormatId, StringComparison.Ordinal);

        private static bool ImplementsRpcCodecAdapter(INamedTypeSymbol type)
            => type.AllInterfaces.Any(static item =>
                item.Name == "IRpcCodecAdapter" &&
                item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions");

        private static bool IsValidAdapterType(INamedTypeSymbol type)
            => IsEffectivelyPublic(type) &&
               type.IsSealed &&
               type.InstanceConstructors.Any(static constructor =>
                   constructor.DeclaredAccessibility == Accessibility.Public &&
                   constructor.Parameters.Length == 0) &&
               ImplementsRpcCodecAdapter(type);

        private bool HasResolvableCustomCodec(ITypeSymbol type)
        {
            var candidates = new List<ITypeSymbol>();
            foreach (var attribute in type.GetAttributes())
            {
                if (!IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAttribute"))
                    continue;
                if (attribute.ConstructorArguments.Length != 1 ||
                    attribute.ConstructorArguments[0].Value is not ITypeSymbol codec)
                {
                    return false;
                }
                candidates.Add(codec);
            }

            if (_customCodecBindings.TryGetValue(NormalizeAdapterTarget(type), out var assemblyBinding))
                candidates.Add(assemblyBinding.CodecType);
            if (candidates.Count == 0)
                return false;

            ITypeSymbol? selected = null;
            foreach (var candidate in candidates)
            {
                if (selected is null)
                {
                    selected = candidate;
                    continue;
                }
                if (!SymbolEqualityComparer.Default.Equals(selected, candidate))
                    return false;
            }

            return selected is not null && IsValidCustomCodec(selected, type);
        }

        private static bool IsValidCustomCodec(ITypeSymbol codecType, ITypeSymbol targetType)
        {
            if (codecType is not INamedTypeSymbol named ||
                HasTypeParameter(named) ||
                !IsEffectivelyPublic(named) ||
                !named.IsSealed ||
                !named.InstanceConstructors.Any(static constructor =>
                    constructor.DeclaredAccessibility == Accessibility.Public &&
                    constructor.Parameters.Length == 0))
            {
                return false;
            }

            if (!named.AllInterfaces.Any(item =>
                    item.Name == "IRpcCodec" &&
                    item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions" &&
                    item is INamedTypeSymbol { IsGenericType: true } generic &&
                    generic.TypeArguments.Length == 1 &&
                    SymbolEqualityComparer.Default.Equals(generic.TypeArguments[0], targetType)))
            {
                return false;
            }

            var identity = named.GetAttributes().FirstOrDefault(static attribute =>
                IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecImplementationAttribute"));
            return identity is not null &&
                   identity.ConstructorArguments.Length == 2 &&
                   identity.ConstructorArguments[0].Value is string wireFormatId &&
                   identity.ConstructorArguments[1].Value is string schemaId &&
                   IsStableIdentity(wireFormatId) &&
                   IsStableIdentity(schemaId);
        }

        private CustomCodecRegistration? ValidateCustomCodec(
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
                SymbolEqualityComparer.Default.Equals(generic.TypeArguments[0], targetType));
            if (!implementsTargetCodec)
            {
                Report(DtoDiagnosticKind.CustomCodecTypeInvalid, codecType,
                    $"custom Codec must implement IRpcCodec<{GetTypeName(targetType)}>", location);
                return null;
            }

            var identity = named.GetAttributes().FirstOrDefault(static attribute =>
                IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecImplementationAttribute"));
            if (identity is null ||
                identity.ConstructorArguments.Length != 2 ||
                identity.ConstructorArguments[0].Value is not string wireFormatId ||
                identity.ConstructorArguments[1].Value is not string schemaId ||
                !IsStableIdentity(wireFormatId) ||
                !IsStableIdentity(schemaId))
            {
                Report(DtoDiagnosticKind.CustomCodecIdentityInvalid, codecType,
                    "custom Codec must declare stable ASCII WireFormatId and SchemaId via [RpcCodecImplementation]", location);
                return null;
            }

            return new CustomCodecRegistration(named, wireFormatId, schemaId, location);
        }

        private bool TrySelectCustomCodec(ITypeSymbol type, out CustomCodecRegistration? selected)
        {
            var candidates = new List<(ITypeSymbol Codec, Location Location)>();
            foreach (var attribute in type.GetAttributes())
            {
                if (!IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAttribute"))
                    continue;

                var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ??
                    type.Locations.FirstOrDefault() ?? Location.None;
                if (attribute.ConstructorArguments.Length != 1 ||
                    attribute.ConstructorArguments[0].Value is not ITypeSymbol codec)
                {
                    Report(DtoDiagnosticKind.CustomCodecBindingInvalid, type,
                        "type-level RpcCodec requires only codecType", location);
                    selected = null;
                    return true;
                }
                candidates.Add((codec, location));
            }

            if (_customCodecBindings.TryGetValue(NormalizeAdapterTarget(type), out var assemblyBinding))
                candidates.Add((assemblyBinding.CodecType, assemblyBinding.Location));

            if (candidates.Count == 0)
            {
                selected = null;
                return false;
            }

            var distinct = new List<ITypeSymbol>();
            foreach (var candidate in candidates)
            {
                if (!distinct.Any(existing => SymbolEqualityComparer.Default.Equals(existing, candidate.Codec)))
                    distinct.Add(candidate.Codec);
            }
            if (distinct.Count != 1)
            {
                Report(DtoDiagnosticKind.CustomCodecSelectionConflict, type,
                    "the target selects multiple different custom Codec implementations", candidates[0].Location);
                selected = null;
                _failed.Add(GetTypeName(type));
                return true;
            }

            selected = ValidateCustomCodec(distinct[0], type, candidates[0].Location);
            if (selected is null)
                _failed.Add(GetTypeName(type));
            return true;
        }

        private static bool IsEffectivelyPublic(INamedTypeSymbol type)
        {
            for (var current = type; current is not null; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility != Accessibility.Public)
                    return false;
            }
            return true;
        }

        private static bool InheritsFromAttribute(ITypeSymbol type)
        {
            for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            {
                if (current.Name == "Attribute" && current.ContainingNamespace.ToDisplayString() == "System")
                    return true;
            }
            return false;
        }

        private static bool IsStableIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            foreach (var character in value)
            {
                if (character < 0x21 || character > 0x7E)
                    return false;
            }
            return true;
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

        private static bool IsBuiltin(ITypeSymbol type)
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
                element is null)
            {
                return false;
            }
            return IsBuiltinBlitElement(element);
        }

        private static ITypeSymbol NormalizeAdapterTarget(ITypeSymbol type)
            => type is INamedTypeSymbol
            {
                IsTupleType: true,
                TupleUnderlyingType: { } underlying
            }
                ? underlying
                : type;

        private static bool IsBuiltinBlitElement(ITypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Enum)
                return false;
            var name = type.ToDisplayString();
            return name is "bool" or "byte" or "sbyte" or "short" or "ushort" or "char" or
                "System.Half" or "int" or "uint" or "float" or "System.Text.Rune" or
                "long" or "ulong" or "double" or "System.Guid" or "decimal" or
                "System.DateTimeOffset" or "System.DateTime" or "System.DateOnly" or
                "System.TimeOnly" or "System.TimeSpan" or "System.Int128" or "System.UInt128" or
                "System.Index" or "System.Range";
        }

        private static GeneratedMemberKind GetMemberKind(
            ITypeSymbol type,
            out ITypeSymbol? fixedType,
            out int fixedSize)
        {
            fixedType = null;
            fixedSize = GetFixedSize(type);
            if (fixedSize != 0)
            {
                fixedType = type;
                return GeneratedMemberKind.Fixed;
            }
            if (type.SpecialType == SpecialType.System_String)
                return GeneratedMemberKind.String;
            if (type is INamedTypeSymbol nullable &&
                nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                fixedSize = GetFixedSize(nullable.TypeArguments[0]);
                if (fixedSize != 0)
                {
                    fixedType = nullable.TypeArguments[0];
                    return GeneratedMemberKind.NullableFixed;
                }
            }
            return GeneratedMemberKind.Complex;
        }

        private static int GetFixedSize(ITypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol { EnumUnderlyingType: { } underlying })
                return GetFixedSize(underlying);
            var specialSize = type.SpecialType switch
            {
                SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
                SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Char => 2,
                SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => 4,
                SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double => 8,
                SpecialType.System_Decimal => 16,
                _ => 0
            };
            if (specialSize != 0)
                return specialSize;
            var name = type.ToDisplayString();
            return name switch
            {
                "System.Half" => 2,
                "System.Text.Rune" or "System.Index" or "System.DateOnly" => 4,
                "System.Range" or "System.DateTime" or "System.TimeOnly" or "System.TimeSpan" => 8,
                "System.Guid" or "System.DateTimeOffset" or "System.Int128" or "System.UInt128" => 16,
                _ => 0
            };
        }

        private static ITypeSymbol GetMemberType(ISymbol member) => member switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => throw new InvalidOperationException("Unsupported DTO member symbol.")
        };

        private static bool IsAssignable(ISymbol member) => member switch
        {
            IFieldSymbol field => !field.IsReadOnly,
            IPropertySymbol property => property.SetMethod?.DeclaredAccessibility == Accessibility.Public,
            _ => false
        };

        private static bool IsRequired(ISymbol member)
            => HasAttribute(member, "SharpLink.Sdk", "RpcRequiredAttribute") ||
               IsCompilerRequired(member);

        private static bool IsCompilerRequired(ISymbol member)
            => member is IFieldSymbol { IsRequired: true } or
               IPropertySymbol { IsRequired: true };

        private static bool IsNonNullableReference(ISymbol member, ITypeSymbol type)
        {
            if (!type.IsReferenceType)
                return false;
            return member switch
            {
                IFieldSymbol field => field.NullableAnnotation == NullableAnnotation.NotAnnotated,
                IPropertySymbol property => property.NullableAnnotation == NullableAnnotation.NotAnnotated,
                _ => false
            };
        }

        private static bool IsNullable(ISymbol member, ITypeSymbol type)
        {
            if (type is INamedTypeSymbol nullable &&
                nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                return true;
            }
            if (!type.IsReferenceType)
                return false;
            return member switch
            {
                IFieldSymbol field => field.NullableAnnotation != NullableAnnotation.NotAnnotated,
                IPropertySymbol property => property.NullableAnnotation != NullableAnnotation.NotAnnotated,
                _ => true
            };
        }

        private static uint GetMemberId(ISymbol member, out bool valid, out bool hasExplicitId)
        {
            foreach (var attribute in member.GetAttributes())
            {
                if (!IsAttribute(attribute, "SharpLink.Sdk", "RpcMemberAttribute"))
                    continue;
                if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int id &&
                    id is > 0 and <= 0x1FFF_FFFF)
                {
                    hasExplicitId = true;
                    valid = true;
                    return (uint)id;
                }
                hasExplicitId = true;
                valid = false;
                return 0;
            }

            var hash = 2166136261U;
            foreach (var character in member.Name)
            {
                hash ^= character;
                hash *= 16777619U;
            }
            hash &= 0x1FFF_FFFFU;
            if (hash == 0)
                hash = 1;
            hasExplicitId = false;
            valid = true;
            return hash;
        }

        private void Report(
            DtoDiagnosticKind kind,
            ITypeSymbol type,
            string detail,
            Location? location = null)
        {
            var typeName = GetTypeName(type);
            var key = $"{kind}|{typeName}|{detail}";
            if (!_diagnosticKeys.Add(key))
                return;
            _diagnostics.Add(new DtoDiagnosticModel(
                kind,
                typeName,
                detail,
                location ?? type.Locations.FirstOrDefault()));
        }

        private void Report(
            DtoDiagnosticKind kind,
            IAssemblySymbol assembly,
            string detail,
            Location? location = null)
        {
            var key = $"{kind}|{assembly.Identity}|{detail}";
            if (!_diagnosticKeys.Add(key))
                return;
            _diagnostics.Add(new DtoDiagnosticModel(
                kind,
                assembly.Identity.ToString(),
                detail,
                location));
        }

        private static bool HasAttribute(ISymbol symbol, string ns, string name)
            => symbol.GetAttributes().Any(attribute => IsAttribute(attribute, ns, name));

        private static string EscapeIdentifier(string identifier)
            => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(identifier) !=
               Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
                ? "@" + identifier
                : identifier;

        private static string GetCodecName(string typeName, bool contractMode)
            => "__SharpLinkGeneratedCodec_" + ComputeHash((contractMode ? "contract|" : "standalone|") + typeName).ToString("X16", InvariantCulture);

        private static string GetSchemaId(string typeName, string schema)
            => typeName + ":" + ComputeHash(schema).ToString("X16", InvariantCulture);

        private static ulong ComputeHash(string value)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= prime;
            }
            return hash;
        }

        private sealed record AnalyzedMember(
            ISymbol Symbol,
            ITypeSymbol Type,
            uint FieldId,
            GeneratedMemberKind Kind,
            ITypeSymbol? FixedType,
            int FixedSize,
            bool Required,
            bool Nullable,
            bool NonNullableReference,
            bool Assignable,
            bool HasExplicitId,
            string? EnumUnderlyingType);

        private sealed record ExplicitBindingCandidate(
            INamedTypeSymbol ImplementationType,
            Location Location);

        private sealed record AdapterRegistration(
            INamedTypeSymbol AdapterType,
            string AdapterId,
            string WireFormatId,
            ITypeSymbol? SelectorType,
            Location Location);

        private sealed record CustomCodecRegistration(
            INamedTypeSymbol CodecType,
            string WireFormatId,
            string SchemaId,
            Location Location);
    }
}
