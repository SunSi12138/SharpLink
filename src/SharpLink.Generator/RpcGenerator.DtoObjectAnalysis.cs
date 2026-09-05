namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
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
