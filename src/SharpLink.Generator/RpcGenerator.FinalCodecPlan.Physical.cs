namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
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
                var location = named.Locations.FirstOrDefault(static item => item.IsInSource) ?? Location.None;
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

                int? offset = effective.Kind == FinalEffectiveLayoutKind.Explicit
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
    }
}
