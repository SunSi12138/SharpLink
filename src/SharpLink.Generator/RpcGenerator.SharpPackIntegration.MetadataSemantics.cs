namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void ValidateSharpPackSidecarMetadata(
        Compilation compilation,
        SharpPackSidecarAnalysis analysis,
        ITypeSymbol rootType)
    {
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        ValidateSharpPackSidecarMetadataCore(
            compilation,
            analysis,
            rootType,
            GetTypeName(rootType),
            visited);
    }

    private static void ValidateSharpPackSidecarMetadataCore(
        Compilation compilation,
        SharpPackSidecarAnalysis analysis,
        ITypeSymbol type,
        string path,
        HashSet<ITypeSymbol> visited)
    {
        if (!visited.Add(type))
            return;

        if (type is IArrayTypeSymbol array)
        {
            ValidateSharpPackSidecarMetadataCore(
                compilation,
                analysis,
                array.ElementType,
                path + " -> element",
                visited);
            return;
        }

        if (type is not INamedTypeSymbol named ||
            named.TypeKind == TypeKind.Enum ||
            named.IsUnmanagedType ||
            HasVerifiableSharpPackSupportForMetadataValidation(compilation, named))
        {
            return;
        }

        var metadataName = GetSharpPackMetadataName(named);
        if (SharpPackWellKnownManagedTypes.Contains(metadataName))
            return;

        if (named.IsGenericType &&
            SharpPackKnownGenericTypes.Contains(
                GetSharpPackMetadataName(named.OriginalDefinition)))
        {
            for (var index = 0; index < named.TypeArguments.Length; index++)
            {
                ValidateSharpPackSidecarMetadataCore(
                    compilation,
                    analysis,
                    named.TypeArguments[index],
                    path + $" -> type argument {index + 1}",
                    visited);
            }
            return;
        }

        if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return;

        if (TryFindUnsupportedSharpPackSidecarMetadata(
                named,
                out var detail,
                out var location))
        {
            analysis.Report(
                GetTypeName(named),
                $"{path}: {detail}. Generated SharpPack sidecars currently preserve only the ordinary Object/Sequential metadata subset; use an authoritative SharpPack formatter, explicit RpcCodec/RpcCodecAdapter, or a supported DTO wrapper.",
                location ?? named.Locations.FirstOrDefault());
        }

        var hierarchy = new Stack<INamedTypeSymbol>();
        for (var current = named;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            hierarchy.Push(current);
        }

        var selectedMembers = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
        while (hierarchy.Count != 0)
        {
            var current = hierarchy.Pop();
            foreach (var symbol in current.GetMembers())
            {
                if (symbol.IsStatic || symbol.IsImplicitlyDeclared || !symbol.CanBeReferencedByName ||
                    symbol is not (IFieldSymbol or IPropertySymbol) ||
                    HasAttribute(symbol, "SharpPack", "SharpPackIgnoreAttribute"))
                {
                    continue;
                }

                if (symbol is IPropertySymbol property &&
                    (property.IsIndexer || property.GetMethod is null))
                {
                    continue;
                }

                var publicForRead = symbol.DeclaredAccessibility == Accessibility.Public &&
                    (symbol is not IPropertySymbol readableProperty ||
                     readableProperty.GetMethod?.DeclaredAccessibility == Accessibility.Public);
                if (!publicForRead)
                    continue;

                selectedMembers[symbol.Name] = symbol;
            }
        }

        foreach (var symbol in selectedMembers.Values)
        {
            var memberType = GetMemberType(symbol);
            if (memberType is null)
                continue;

            ValidateSharpPackSidecarMetadataCore(
                compilation,
                analysis,
                memberType,
                path + " -> member '" + symbol.Name + "'",
                visited);
        }
    }

    private static bool HasVerifiableSharpPackSupportForMetadataValidation(
        Compilation compilation,
        INamedTypeSymbol type)
    {
        var isCurrentAssembly = SymbolEqualityComparer.Default.Equals(
            type.ContainingAssembly,
            compilation.Assembly);
        if (isCurrentAssembly &&
            (HasAttribute(type, "SharpPack", "SharpPackableAttribute") ||
             HasAttribute(type, "SharpPack", "SharpPackUnionAttribute")))
        {
            return true;
        }

        foreach (var iface in type.AllInterfaces)
        {
            var metadataName = GetSharpPackMetadataName(iface.OriginalDefinition);
            if (string.Equals(
                    metadataName,
                    "SharpPack.ISharpPackable`1",
                    StringComparison.Ordinal) ||
                string.Equals(
                    metadataName,
                    "SharpPack.ISharpPackFormatterFactory`1",
                    StringComparison.Ordinal) ||
                string.Equals(
                    metadataName,
                    "SharpPack.ISharpPackContextFormatterFactory`1",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindUnsupportedSharpPackSidecarMetadata(
        INamedTypeSymbol type,
        out string detail,
        out Location? location)
    {
        for (var current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            foreach (var attribute in current.GetAttributes())
            {
                if (IsAttribute(attribute, "SharpPack", "SharpPackableAttribute") &&
                    TryGetUnsupportedSharpPackableSetting(attribute, out var setting))
                {
                    detail = $"type '{GetTypeName(current)}' uses unsupported SharpPack metadata {setting}";
                    location = current.Locations.FirstOrDefault();
                    return true;
                }

                if (IsAttribute(attribute, "SharpPack", "SharpPackUnionAttribute") ||
                    IsAttribute(attribute, "SharpPack", "SharpPackUnionFormatterAttribute"))
                {
                    detail = $"type '{GetTypeName(current)}' uses SharpPack union metadata that the sidecar wire format does not reproduce";
                    location = current.Locations.FirstOrDefault();
                    return true;
                }
            }

            foreach (var constructor in current.InstanceConstructors)
            {
                if (!HasAttribute(
                        constructor,
                        "SharpPack",
                        "SharpPackConstructorAttribute"))
                {
                    continue;
                }

                detail = $"constructor '{current.Name}' is selected with [SharpPackConstructor], whose construction semantics are not reproduced by sidecars";
                location = constructor.Locations.FirstOrDefault();
                return true;
            }

            foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
            {
                var callback = GetSharpPackCallbackAttributeName(method);
                if (callback is null)
                    continue;

                detail = $"method '{method.Name}' uses [{callback}], but sidecars do not invoke SharpPack serialization callbacks";
                location = method.Locations.FirstOrDefault();
                return true;
            }

            foreach (var member in current.GetMembers())
            {
                if (member is not (IFieldSymbol or IPropertySymbol) ||
                    !HasAttribute(
                        member,
                        "SharpPack",
                        "SuppressDefaultInitializationAttribute"))
                {
                    continue;
                }

                detail = $"member '{member.Name}' uses [SuppressDefaultInitialization], whose deserialization initialization semantics are not reproduced by sidecars";
                location = member.Locations.FirstOrDefault();
                return true;
            }
        }

        detail = string.Empty;
        location = null;
        return false;
    }

    private static bool TryGetUnsupportedSharpPackableSetting(
        AttributeData attribute,
        out string setting)
    {
        foreach (var argument in attribute.ConstructorArguments)
        {
            if (argument.Type is not INamedTypeSymbol enumType)
            {
                setting = "an unrecognized [SharpPackable] constructor shape";
                return true;
            }

            var enumMetadataName = GetSharpPackMetadataName(enumType);
            if (string.Equals(
                    enumMetadataName,
                    "SharpPack.GenerateType",
                    StringComparison.Ordinal))
            {
                if (argument.Value is not int value || value != 0)
                {
                    setting = $"GenerateType value '{argument.Value ?? "<unknown>"}'";
                    return true;
                }
                continue;
            }

            if (string.Equals(
                    enumMetadataName,
                    "SharpPack.SerializeLayout",
                    StringComparison.Ordinal))
            {
                if (argument.Value is not int value || value != 0)
                {
                    setting = $"SerializeLayout value '{argument.Value ?? "<unknown>"}'";
                    return true;
                }
                continue;
            }

            setting = "an unrecognized [SharpPackable] constructor shape";
            return true;
        }

        setting = string.Empty;
        return false;
    }

    private static string? GetSharpPackCallbackAttributeName(IMethodSymbol method)
    {
        if (HasAttribute(method, "SharpPack", "SharpPackOnSerializingAttribute"))
            return "SharpPackOnSerializing";
        if (HasAttribute(method, "SharpPack", "SharpPackOnSerializedAttribute"))
            return "SharpPackOnSerialized";
        if (HasAttribute(method, "SharpPack", "SharpPackOnDeserializingAttribute"))
            return "SharpPackOnDeserializing";
        if (HasAttribute(method, "SharpPack", "SharpPackOnDeserializedAttribute"))
            return "SharpPackOnDeserialized";
        return null;
    }

    private static string GetSharpPackMetadataName(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return string.IsNullOrEmpty(ns) ? type.MetadataName : ns + "." + type.MetadataName;
    }
}
