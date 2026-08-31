namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private RpcHashValue GetAdapterClosedCodecSemanticIdentity(GeneratedCodecModel model)
        {
            if (!TryResolveReachableType(model.TypeName, out var targetType))
            {
                throw new InvalidOperationException(
                    $"Final RPC Codec graph cannot resolve adapter target '{model.TypeName}' while hashing its closed Codec semantics.");
            }

            var parts = new List<string> { "adapter-closed-target/v1" };
            AppendAdapterClosedTargetShape(
                targetType,
                parts,
                new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default),
                depth: 0);
            return Hashing.GetSemanticHash(parts.ToArray());
        }

        private void AppendAdapterClosedTargetShape(
            ITypeSymbol type,
            List<string> parts,
            HashSet<ITypeSymbol> stack,
            int depth)
        {
            var typeName = GetTypeName(type);
            parts.Add("type:" + typeName);
            if (depth > MaximumDepth)
            {
                parts.Add("depth-limit");
                return;
            }
            if (!stack.Add(type))
            {
                parts.Add("recursive:" + typeName);
                return;
            }

            try
            {
                AppendAttributes(type, parts, "type-attr:");
                if (type is IArrayTypeSymbol array)
                {
                    parts.Add("array-rank:" + array.Rank.ToString(InvariantCulture));
                    AppendAdapterClosedTargetShape(array.ElementType, parts, stack, depth + 1);
                    return;
                }
                if (type is not INamedTypeSymbol named)
                    return;

                if (named.TypeKind == TypeKind.Enum)
                {
                    parts.Add("enum-underlying:" + GetTypeName(named.EnumUnderlyingType!));
                    foreach (var enumField in named.GetMembers().OfType<IFieldSymbol>()
                                 .Where(static field => field.HasConstantValue)
                                 .OrderBy(static field => field.Name, StringComparer.Ordinal))
                    {
                        parts.Add("enum:" + enumField.Name + "=" +
                                  (Convert.ToString(enumField.ConstantValue, InvariantCulture) ?? "null"));
                    }
                    return;
                }

                foreach (var argument in named.TypeArguments)
                    AppendAdapterClosedTargetShape(argument, parts, stack, depth + 1);

                if (named.BaseType is { SpecialType: not SpecialType.System_Object and not SpecialType.System_ValueType } baseType)
                {
                    parts.Add("base");
                    AppendAdapterClosedTargetShape(baseType, parts, stack, depth + 1);
                }

                var members = named.GetMembers()
                    .Where(static member => !member.IsStatic && member.DeclaredAccessibility == Accessibility.Public)
                    .Where(static member =>
                        member is IFieldSymbol { IsConst: false } or
                        IPropertySymbol { IsIndexer: false })
                    .OrderBy(static member => member.Kind.ToString(), StringComparer.Ordinal)
                    .ThenBy(static member => member.Name, StringComparer.Ordinal)
                    .ThenBy(static member => member.ToDisplayString(), StringComparer.Ordinal);
                foreach (var member in members)
                {
                    var memberType = member switch
                    {
                        IFieldSymbol memberField => memberField.Type,
                        IPropertySymbol memberProperty => memberProperty.Type,
                        _ => throw new InvalidOperationException("Unexpected adapter target member kind.")
                    };
                    parts.Add("member:" + member.Kind + ":" + member.Name + ":" + GetTypeName(memberType));
                    if (member is IFieldSymbol memberField)
                        parts.Add(memberField.IsReadOnly ? "readonly" : "mutable");
                    else if (member is IPropertySymbol memberProperty)
                    {
                        parts.Add(memberProperty.GetMethod?.DeclaredAccessibility == Accessibility.Public ? "get" : "no-get");
                        parts.Add(memberProperty.SetMethod?.DeclaredAccessibility == Accessibility.Public ? "set" : "no-set");
                    }
                    AppendAttributes(member, parts, "member-attr:");
                    AppendAdapterClosedTargetShape(memberType, parts, stack, depth + 1);
                }
            }
            finally
            {
                stack.Remove(type);
            }
        }

        private static void AppendAttributes(ISymbol symbol, List<string> parts, string prefix)
        {
            foreach (var attribute in symbol.GetAttributes()
                         .Select(static attribute => attribute.ToString())
                         .OrderBy(static value => value, StringComparer.Ordinal))
            {
                parts.Add(prefix + attribute);
            }
        }
    }
}
