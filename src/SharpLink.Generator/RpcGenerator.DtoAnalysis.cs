namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static DtoGenerationResult AnalyzeGeneratedCodecs(
        Compilation compilation,
        CancellationToken cancellationToken)
        => new DtoAnalysisState(compilation, cancellationToken).Analyze();

    private sealed class DtoAnalysisState
    {
        private const int MaximumDepth = 64;
        private readonly Compilation _compilation;
        private readonly CancellationToken _cancellationToken;
        private readonly HashSet<string> _allowedAssemblyNames;
        private readonly HashSet<ITypeSymbol> _externalTypes = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, GeneratedCodecModel> _models = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GeneratedEnumModel> _enums = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);
        private readonly List<DtoDiagnosticModel> _diagnostics = [];

        public DtoAnalysisState(Compilation compilation, CancellationToken cancellationToken)
        {
            _compilation = compilation;
            _cancellationToken = cancellationToken;
            _allowedAssemblyNames = ResolveReferenceAssemblyNames(compilation);
            _allowedAssemblyNames.Add(compilation.Assembly.Identity.Name);
            CollectAssemblyExternalTypes();
        }

        public DtoGenerationResult Analyze()
        {
            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(_compilation.Assembly.GlobalNamespace, roots);
            CollectReferencedContractRoots(roots);

            foreach (var root in roots.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Visit(root.Value, [], 0);
            }

            return new DtoGenerationResult(
                _models.Values.OrderBy(static model => model.TypeName, StringComparer.Ordinal).ToImmutableArray(),
                _diagnostics.ToImmutableArray(),
                _enums.Values.OrderBy(static item => item.TypeName, StringComparer.Ordinal).ToImmutableArray());
        }

        private void CollectCurrentAssemblyRoots(
            INamespaceSymbol namespaceSymbol,
            Dictionary<string, ITypeSymbol> roots)
        {
            foreach (var type in namespaceSymbol.GetTypeMembers())
                CollectCurrentAssemblyRoots(type, roots);
            foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
                CollectCurrentAssemblyRoots(nestedNamespace, roots);
        }

        private void CollectCurrentAssemblyRoots(
            INamedTypeSymbol type,
            Dictionary<string, ITypeSymbol> roots)
        {
            if (HasAttribute(type, "SharpLink.Sdk", "RpcSerializableAttribute"))
                AddRoot(roots, type);
            if (type.TypeKind == TypeKind.Interface && HasRpcContractAttribute(type))
                CollectContractPayloadRoots(type, roots);

            foreach (var nested in type.GetTypeMembers())
                CollectCurrentAssemblyRoots(nested, roots);
        }

        private void CollectReferencedContractRoots(Dictionary<string, ITypeSymbol> roots)
        {
            foreach (var reference in _compilation.References)
            {
                if (_compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly ||
                    !_allowedAssemblyNames.Contains(assembly.Identity.Name))
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
            foreach (var method in contract.GetMembers().OfType<IMethodSymbol>()
                         .Where(static method => method.MethodKind == MethodKind.Ordinary))
            {
                foreach (var parameter in method.Parameters)
                {
                    if (IsCancellationTokenParameter(parameter) || IsCallOptionsParameter(parameter))
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

        private void CollectAssemblyExternalTypes()
        {
            foreach (var attribute in _compilation.Assembly.GetAttributes())
            {
                if (!IsAttribute(attribute, "SharpLink.Sdk", "RpcExternalCodecAttribute") ||
                    attribute.ConstructorArguments.Length == 0)
                {
                    continue;
                }
                if (attribute.ConstructorArguments[0].Value is ITypeSymbol type)
                    _externalTypes.Add(type);
            }
        }

        private void Visit(ITypeSymbol type, List<ITypeSymbol> stack, int depth)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            CollectEnums(type);
            var typeName = GetTypeName(type);
            if (_models.ContainsKey(typeName) || _failed.Contains(typeName) || IsBuiltin(type))
                return;
            if (depth > MaximumDepth)
            {
                Report(DtoDiagnosticKind.Depth, type, $"more than {MaximumDepth} nested types");
                _failed.Add(typeName);
                return;
            }
            if (type.SpecialType == SpecialType.System_Object ||
                type.TypeKind is TypeKind.Delegate or TypeKind.Dynamic or TypeKind.Pointer or TypeKind.FunctionPointer)
            {
                Report(DtoDiagnosticKind.Unsupported, type, "object, delegate, dynamic, pointer, and function-pointer values require an explicit typed Codec");
                _failed.Add(typeName);
                return;
            }
            if (IsExplicitExternal(type))
                return;
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
                    GetCodecName(typeName),
                    GetSchemaId(typeName, collectionKind.ToString()),
                    collectionKind,
                    type.IsReferenceType,
                    ImmutableArray<GeneratedMemberModel>.Empty,
                    ImmutableArray<string>.Empty,
                    elementType is null ? null : GetTypeName(elementType),
                    keyType is null ? null : GetTypeName(keyType),
                    valueType is null ? null : GetTypeName(valueType),
                    GetAssemblyDependencies([type]),
                    type.Locations.FirstOrDefault());
                return;
            }

            if (IsThirdPartyType(type))
                return;

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
            if (named.TypeKind == TypeKind.Class && !named.IsSealed && !named.IsRecord)
            {
                Report(DtoDiagnosticKind.Unsupported, type, "classes must be sealed; use RpcExternalCodec for polymorphic graphs");
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
                    EscapeIdentifier(member.Symbol.Name),
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
                schema.Append('|').Append(member.FieldId).Append(':').Append(member.TypeName).Append(':').Append(member.Required);
            var dependencyTypes = new List<ITypeSymbol>(analyzedMembers.Count + 1) { type };
            dependencyTypes.AddRange(analyzedMembers.Select(static member => member.Type));
            _models[typeName] = new GeneratedCodecModel(
                typeName,
                GetCodecName(typeName),
                GetSchemaId(typeName, schema.ToString()),
                GeneratedCodecKind.Dto,
                named.IsReferenceType,
                generatedMembers,
                constructorMembers.ToImmutableArray(),
                null,
                null,
                null,
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
                _allowedAssemblyNames.Contains(assembly.Identity.Name))
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
                return true;
            }

            var memberByName = members.ToDictionary(
                static member => member.Symbol.Name,
                StringComparer.OrdinalIgnoreCase);
            foreach (var constructor in type.InstanceConstructors
                         .Where(IsConstructorAccessible)
                         .OrderBy(static constructor => constructor.Parameters.Length)
                         .ThenBy(static constructor => constructor.ToDisplayString(), StringComparer.Ordinal))
            {
                var mapped = new List<string>(constructor.Parameters.Length);
                var valid = true;
                foreach (var parameter in constructor.Parameters)
                {
                    if (parameter.Name is null ||
                        !memberByName.TryGetValue(parameter.Name, out var member) ||
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
                constructorMembers = mapped;
                return true;
            }

            constructorMembers = [];
            return false;
        }

        private bool IsConstructorAccessible(IMethodSymbol constructor)
        {
            if (constructor.DeclaredAccessibility == Accessibility.Public)
                return true;
            if (!SymbolEqualityComparer.Default.Equals(constructor.ContainingAssembly, _compilation.Assembly))
                return false;
            return constructor.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal;
        }

        private bool IsExplicitExternal(ITypeSymbol type)
        {
            if (_externalTypes.Contains(type) || HasAttribute(type, "SharpLink.Sdk", "RpcExternalCodecAttribute"))
                return true;
            return HasAttribute(type, "MemoryPack", "MemoryPackableAttribute");
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
            if (type.SpecialType == SpecialType.System_String || GetFixedSize(type) != 0)
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
               member is IPropertySymbol { IsRequired: true };

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

        private static bool HasAttribute(ISymbol symbol, string ns, string name)
            => symbol.GetAttributes().Any(attribute => IsAttribute(attribute, ns, name));

        private static string GetTypeName(ITypeSymbol type)
            => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        private static string EscapeIdentifier(string identifier)
            => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(identifier) !=
               Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
                ? "@" + identifier
                : identifier;

        private static string GetCodecName(string typeName)
            => "__SharpLinkGeneratedCodec_" + ComputeHash(typeName).ToString("X16", InvariantCulture);

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
    }
}
