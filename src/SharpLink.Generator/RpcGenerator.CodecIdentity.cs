namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private readonly Dictionary<string, RpcHashValue?> _opaqueSemanticIdentityCache =
            new(StringComparer.Ordinal);

        internal ImmutableArray<GeneratedCodecHashModel> BuildFinalCodecHashes(
            bool includeSerializable,
            bool includeContracts)
        {
            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(
                _compilation.Assembly.GlobalNamespace,
                roots,
                includeSerializable,
                includeContracts);

            var reachable = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var root in roots.Values)
                CollectFinalBindingTypes(root, reachable, seen, 0);

            var cache = new Dictionary<string, RpcHashValue>(StringComparer.Ordinal);
            return reachable
                .Where(pair => !_failed.Contains(pair.Key))
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                {
                    var hash = GetFinalCodecHash(pair.Value, cache, new HashSet<string>(StringComparer.Ordinal));
                    return new GeneratedCodecHashModel(pair.Key, hash.High, hash.Low);
                })
                .ToImmutableArray();
        }

        private RpcHashValue GetFinalCodecHash(
            ITypeSymbol type,
            Dictionary<string, RpcHashValue> cache,
            HashSet<string> stack)
        {
            var typeName = GetTypeName(type);
            if (cache.TryGetValue(typeName, out var cached))
                return cached;
            if (!stack.Add(typeName))
                return Hashing.GetSemanticHash("codec/v1", "recursive", typeName);

            RpcHashValue result;
            if (TryGetFrameworkPrimitiveCodecHash(type, cache, stack, out result))
            {
                stack.Remove(typeName);
                cache[typeName] = result;
                return result;
            }

            if (_models.TryGetValue(typeName, out var model))
            {
                result = GetGeneratedCodecHash(model, cache, stack);
                stack.Remove(typeName);
                cache[typeName] = result;
                return result;
            }

            if (TryGetReferencedGeneratedCodecHash(type, out result))
            {
                stack.Remove(typeName);
                cache[typeName] = result;
                return result;
            }

            if (TryGetCollection(type, out var collectionKind, out var elementType, out var keyType, out var valueType))
            {
                var parts = new List<string>
                {
                    "codec/v1",
                    "collection",
                    collectionKind.ToString()
                };
                if (elementType is not null)
                    parts.Add(GetFinalCodecHash(elementType, cache, stack).ToHex());
                if (keyType is not null)
                    parts.Add(GetFinalCodecHash(keyType, cache, stack).ToHex());
                if (valueType is not null)
                    parts.Add(GetFinalCodecHash(valueType, cache, stack).ToHex());
                result = Hashing.GetSemanticHash(parts.ToArray());
            }
            else if (type.IsUnmanagedType && !IsRuntimeSizedUnsafeBlitType(type))
            {
                var layout = new StringBuilder("unsafe-blit/v2|abi:little-endian|native-pointer-width/64");
                AppendUnsafeBlitPhysicalLayout(
                    type,
                    layout,
                    new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
                result = Hashing.GetSemanticHash("codec/v1", layout.ToString());
            }
            else
            {
                throw new InvalidOperationException(
                    $"Final RPC Codec graph cannot resolve deterministic CodecHash metadata for referenced payload '{typeName}'. Rebuild the referenced SharpLink assembly with deterministic identity generation enabled.");
            }

            stack.Remove(typeName);
            cache[typeName] = result;
            return result;
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
                if (!IsAttribute(
                        attribute,
                        "SharpLink.Abstractions",
                        "SharpLinkGeneratedCodecIdentityAttribute") ||
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

        private RpcHashValue GetGeneratedCodecHash(
            GeneratedCodecModel model,
            Dictionary<string, RpcHashValue> cache,
            HashSet<string> stack)
        {
            switch (model.Kind)
            {
                case GeneratedCodecKind.Custom:
                    return Hashing.GetSemanticHash(
                        "codec/v1",
                        "custom-opaque",
                        GetRequiredOpaqueSemanticIdentity(model.CustomCodecType, "custom Codec").ToHex());
                case GeneratedCodecKind.Adapter:
                    return Hashing.GetSemanticHash(
                        "codec/v1",
                        "adapter-closed/v1",
                        model.AdapterId ?? string.Empty,
                        GetRequiredOpaqueSemanticIdentity(model.AdapterType, "Codec Adapter").ToHex(),
                        GetAdapterClosedCodecSemanticIdentity(model).ToHex());
                case GeneratedCodecKind.Dto:
                    {
                        var parts = new List<string>
                        {
                            "codec/v1",
                            "dto",
                            model.IsReferenceType ? "ref" : "value"
                        };
                        foreach (var member in model.Members.OrderBy(static member => member.FieldId))
                        {
                            parts.Add(member.FieldId.ToString(InvariantCulture));
                            parts.Add(member.Kind.ToString());
                            parts.Add(member.Required ? "required" : "optional");
                            parts.Add(member.Nullable ? "nullable" : "non-nullable");
                            parts.Add(member.NonNullableReference ? "non-null-ref" : "other-null-semantics");
                            switch (member.Kind)
                            {
                                case GeneratedMemberKind.String:
                                    parts.Add("string/content/utf8/u32le-byte-length/v1");
                                    parts.Add("string/null/dto-wire-null/v1");
                                    break;
                                case GeneratedMemberKind.Fixed:
                                case GeneratedMemberKind.NullableFixed:
                                    parts.Add(GetFixedMemberSemanticIdentity(member));
                                    break;
                                case GeneratedMemberKind.Complex:
                                    if (!TryResolveReachableType(member.TypeName, out var memberType))
                                    {
                                        throw new InvalidOperationException(
                                            $"Final RPC Codec graph cannot resolve child payload '{member.TypeName}' while hashing '{model.TypeName}'.");
                                    }
                                    parts.Add(GetFinalCodecHash(memberType, cache, stack).ToHex());
                                    break;
                            }
                        }
                        return Hashing.GetSemanticHash(parts.ToArray());
                    }
                default:
                    {
                        var parts = new List<string>
                        {
                            "codec/v1",
                            "collection",
                            model.Kind.ToString()
                        };
                        AppendChild(model.ElementType);
                        AppendChild(model.KeyType);
                        AppendChild(model.ValueType);
                        return Hashing.GetSemanticHash(parts.ToArray());

                        void AppendChild(string? childTypeName)
                        {
                            if (childTypeName is null)
                                return;
                            if (!TryResolveReachableType(childTypeName, out var childType))
                            {
                                throw new InvalidOperationException(
                                    $"Final RPC Codec graph cannot resolve child payload '{childTypeName}' while hashing '{model.TypeName}'.");
                            }
                            parts.Add(GetFinalCodecHash(childType, cache, stack).ToHex());
                        }
                    }
            }
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

            var assemblies = new Dictionary<string, IAssemblySymbol>(StringComparer.Ordinal)
            {
                [_compilation.Assembly.Identity.ToString()] = _compilation.Assembly
            };
            var pending = new Queue<IAssemblySymbol>();
            pending.Enqueue(_compilation.Assembly);
            while (pending.Count != 0)
            {
                var assembly = pending.Dequeue();
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
                {
                    var identity = referenced.Identity.ToString();
                    if (assemblies.ContainsKey(identity))
                        continue;
                    assemblies.Add(identity, referenced);
                    pending.Enqueue(referenced);
                }
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

        private static string GetFixedMemberSemanticIdentity(GeneratedMemberModel member)
        {
            var typeName = member.FixedTypeName ?? member.TypeName;
            return string.Join(
                ":",
                "fixed/v1",
                member.FixedSize.ToString(InvariantCulture),
                member.EnumUnderlyingType ?? typeName);
        }

        private bool TryGetFrameworkPrimitiveCodecHash(
            ITypeSymbol type,
            Dictionary<string, RpcHashValue> cache,
            HashSet<string> stack,
            out RpcHashValue hash)
        {
            if (type.TypeKind == TypeKind.Enum &&
                type is INamedTypeSymbol { EnumUnderlyingType: { } enumUnderlying })
            {
                hash = Hashing.GetSemanticHash(
                    "codec/v1",
                    "enum",
                    GetFinalCodecHash(enumUnderlying, cache, stack).ToHex());
                return true;
            }

            if (type is INamedTypeSymbol nullable &&
                nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                nullable.TypeArguments.Length == 1 &&
                IsFrameworkWirePrimitive(nullable.TypeArguments[0]))
            {
                hash = Hashing.GetSemanticHash(
                    "codec/v1",
                    "nullable",
                    GetFinalCodecHash(nullable.TypeArguments[0], cache, stack).ToHex());
                return true;
            }

            if (type.SpecialType == SpecialType.System_String)
            {
                hash = Hashing.GetSemanticHash(
                    "codec/v1",
                    "framework",
                    "string/content/utf8/u32le-byte-length/v1",
                    "string/null/u32-max/v1");
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

            if (token is null && type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte })
                token = "bytes/v1";
            if (token is null)
            {
                token = type.ToDisplayString() switch
                {
                    "System.Half" => "half/fixed2/v1",
                    "System.Text.Rune" => "rune/fixed4/v1",
                    "System.Guid" => "guid/fixed16/v1",
                    "System.DateTimeOffset" => "datetime-offset/fixed16/v1",
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
            }

            if (token is null)
            {
                hash = default;
                return false;
            }

            hash = Hashing.GetSemanticHash("codec/v1", "framework", token);
            return true;
        }

        private void AppendUnsafeBlitPhysicalLayout(
            ITypeSymbol type,
            StringBuilder builder,
            HashSet<ITypeSymbol> stack)
        {
            if (TryAppendPhysicalPrimitive(type, builder))
                return;

            if (type.TypeKind == TypeKind.Enum &&
                type is INamedTypeSymbol { EnumUnderlyingType: { } enumUnderlying })
            {
                builder.Append("|enum");
                AppendUnsafeBlitPhysicalLayout(enumUnderlying, builder, stack);
                return;
            }

            if (type is IPointerTypeSymbol pointer)
            {
                builder.Append("|native-pointer-width/64|pointer|");
                AppendUnsafeBlitPhysicalLayout(pointer.PointedAtType, builder, stack);
                return;
            }
            if (type is IFunctionPointerTypeSymbol)
            {
                builder.Append("|native-pointer-width/64|function-pointer");
                return;
            }
            if (type is not INamedTypeSymbol named)
            {
                builder.Append("|unknown-unmanaged");
                return;
            }

            AppendPhysicalLayoutAttribute(builder, named, "System.Runtime.InteropServices.StructLayoutAttribute", stack);
            AppendPhysicalLayoutAttribute(builder, named, "System.Runtime.CompilerServices.InlineArrayAttribute", stack);

            if (!stack.Add(type))
            {
                builder.Append("|recursive");
                return;
            }

            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                named.TypeArguments.Length == 1)
            {
                builder.Append("|nullable-underlying");
                AppendUnsafeBlitPhysicalLayout(named.TypeArguments[0], builder, stack);
                stack.Remove(type);
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
                if (field.IsFixedSizeBuffer)
                    builder.Append("|fixed-buffer:").Append(field.FixedSize.ToString(InvariantCulture));
                AppendPhysicalLayoutAttribute(builder, field, "System.Runtime.InteropServices.FieldOffsetAttribute", stack);
                AppendPhysicalLayoutAttribute(builder, field, "System.Runtime.CompilerServices.FixedBufferAttribute", stack);
                AppendUnsafeBlitPhysicalLayout(field.Type, builder, stack);
            }

            stack.Remove(type);
        }

        private static bool TryAppendPhysicalPrimitive(ITypeSymbol type, StringBuilder builder)
        {
            var token = type.SpecialType switch
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
            }
            if (token is null)
                return false;
            builder.Append('|').Append(token);
            return true;
        }

        private static void AppendPhysicalLayoutAttribute(
            StringBuilder builder,
            ISymbol symbol,
            string attributeName,
            HashSet<ITypeSymbol> stack)
        {
            var attribute = symbol.GetAttributes().FirstOrDefault(item =>
                string.Equals(item.AttributeClass?.ToDisplayString(), attributeName, StringComparison.Ordinal));
            if (attribute is null)
                return;

            builder.Append("|attr:").Append(attributeName);
            foreach (var argument in attribute.ConstructorArguments)
                AppendPhysicalLayoutConstant(builder, argument, stack);
            foreach (var argument in attribute.NamedArguments.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                builder.Append('|').Append(argument.Key).Append('=');
                AppendPhysicalLayoutConstant(builder, argument.Value, stack);
            }
        }

        private static void AppendPhysicalLayoutConstant(
            StringBuilder builder,
            TypedConstant constant,
            HashSet<ITypeSymbol> stack)
        {
            builder.Append(':').Append(constant.Kind.ToString()).Append('=');
            if (constant.Kind == TypedConstantKind.Array)
            {
                builder.Append('[');
                foreach (var item in constant.Values)
                    AppendPhysicalLayoutConstant(builder, item, stack);
                builder.Append(']');
                return;
            }

            if (constant.Value is ITypeSymbol type)
            {
                if (!TryAppendPhysicalPrimitive(type, builder))
                    builder.Append("layout-type");
                return;
            }

            builder.Append(Convert.ToString(constant.Value, InvariantCulture) ?? "null");
        }
    }
}
