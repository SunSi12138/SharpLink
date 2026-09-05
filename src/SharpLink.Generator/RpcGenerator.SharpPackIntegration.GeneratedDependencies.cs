namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private const int SharpPackGenerateTypeCollection = 3;
    private const int SharpPackGenerateTypeNoGenerate = 4;

    private readonly record struct SharpPackGeneratedDependency(
        ITypeSymbol Type,
        string PathSegment,
        Location? Location);

    private static bool IsCurrentCompilationSharpPackGeneratedSupport(
        Compilation compilation,
        INamedTypeSymbol type)
    {
        if (!SymbolEqualityComparer.Default.Equals(
                type.ContainingAssembly,
                compilation.Assembly))
        {
            return false;
        }

        if (HasAttribute(type, "SharpPack", "SharpPackUnionAttribute"))
            return true;

        return TryGetCurrentCompilationSharpPackGenerateType(type, out var generateType) &&
               generateType != SharpPackGenerateTypeNoGenerate;
    }

    private static ImmutableArray<SharpPackGeneratedDependency>
        GetCurrentCompilationSharpPackGeneratedDependencies(
            Compilation compilation,
            INamedTypeSymbol type)
    {
        if (!IsCurrentCompilationSharpPackGeneratedSupport(compilation, type))
            return ImmutableArray<SharpPackGeneratedDependency>.Empty;

        var builder = ImmutableArray.CreateBuilder<SharpPackGeneratedDependency>();
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        void Add(ITypeSymbol dependencyType, string pathSegment, Location? location)
        {
            if (seen.Add(dependencyType))
            {
                builder.Add(new SharpPackGeneratedDependency(
                    dependencyType,
                    pathSegment,
                    location));
            }
        }

        var hasUnion = false;
        foreach (var attribute in type.GetAttributes())
        {
            if (!IsAttribute(attribute, "SharpPack", "SharpPackUnionAttribute"))
                continue;

            hasUnion = true;
            if (attribute.ConstructorArguments.Length >= 2 &&
                attribute.ConstructorArguments[1].Value is ITypeSymbol unionType)
            {
                var tag = attribute.ConstructorArguments[0].Value?.ToString() ?? "?";
                Add(
                    unionType,
                    $"SharpPack union tag {tag}",
                    type.Locations.FirstOrDefault());
            }
        }

        if (hasUnion)
            return builder.ToImmutable();

        if (TryGetCurrentCompilationSharpPackGenerateType(type, out var generateType) &&
            generateType == SharpPackGenerateTypeCollection)
        {
            var collectionContract = SelectSharpPackCollectionContract(type);
            if (collectionContract is null)
                return builder.ToImmutable();

            for (var index = 0; index < collectionContract.TypeArguments.Length; index++)
            {
                Add(
                    collectionContract.TypeArguments[index],
                    $"SharpPack collection type argument {index + 1}",
                    type.Locations.FirstOrDefault());
            }

            return builder.ToImmutable();
        }

        foreach (var member in GetSharpPackGeneratedSerializableMembers(type))
        {
            if (HasSharpPackMemberCustomFormatterAttribute(member))
                continue;

            var memberType = GetMemberType(member);
            if (memberType is null)
                continue;

            Add(
                memberType,
                "SharpPack member '" + member.Name + "'",
                member.Locations.FirstOrDefault());
        }

        return builder.ToImmutable();
    }

    private static INamedTypeSymbol? SelectSharpPackCollectionContract(
        INamedTypeSymbol type)
    {
        INamedTypeSymbol? dictionary = null;
        INamedTypeSymbol? set = null;
        INamedTypeSymbol? collection = null;

        foreach (var iface in type.AllInterfaces)
        {
            if (!iface.IsGenericType)
                continue;

            var metadataName = GetSharpPackMetadataName(iface.OriginalDefinition);
            if (string.Equals(
                    metadataName,
                    "System.Collections.Generic.IDictionary`2",
                    StringComparison.Ordinal))
            {
                dictionary = iface;
            }
            else if (string.Equals(
                         metadataName,
                         "System.Collections.Generic.ISet`1",
                         StringComparison.Ordinal))
            {
                set = iface;
            }
            else if (string.Equals(
                         metadataName,
                         "System.Collections.Generic.ICollection`1",
                         StringComparison.Ordinal))
            {
                collection = iface;
            }
        }

        return dictionary ?? set ?? collection;
    }

    private static bool TryGetCurrentCompilationSharpPackGenerateType(
        INamedTypeSymbol type,
        out int generateType)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (!IsAttribute(attribute, "SharpPack", "SharpPackableAttribute"))
                continue;

            generateType = 0;
            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.Type is not INamedTypeSymbol argumentType)
                    continue;

                var metadataName = GetSharpPackMetadataName(argumentType);
                if (string.Equals(
                        metadataName,
                        "SharpPack.GenerateType",
                        StringComparison.Ordinal))
                {
                    if (argument.Value is int value)
                    {
                        generateType = value;
                        return true;
                    }

                    return false;
                }

                if (string.Equals(
                        metadataName,
                        "SharpPack.SerializeLayout",
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return true;
        }

        generateType = 0;
        return false;
    }

    private static ImmutableArray<ISymbol> GetSharpPackGeneratedSerializableMembers(
        INamedTypeSymbol type)
    {
        var hierarchy = new Stack<INamedTypeSymbol>();
        for (var current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            hierarchy.Push(current);
        }

        var members = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
        while (hierarchy.Count != 0)
        {
            var current = hierarchy.Pop();
            foreach (var symbol in current.GetMembers())
            {
                if (symbol.IsStatic || symbol.IsImplicitlyDeclared ||
                    !symbol.CanBeReferencedByName ||
                    symbol is not (IFieldSymbol or IPropertySymbol))
                {
                    continue;
                }

                if (HasAttribute(symbol, "SharpPack", "SharpPackIgnoreAttribute"))
                    continue;

                var include = HasAttribute(
                    symbol,
                    "SharpPack",
                    "SharpPackIncludeAttribute");
                if (!include && symbol.DeclaredAccessibility != Accessibility.Public)
                    continue;

                if (symbol is IPropertySymbol property &&
                    (property.IsIndexer ||
                     (property.GetMethod is null && property.SetMethod is not null)))
                {
                    continue;
                }

                members[symbol.Name] = symbol;
            }
        }

        return members.Values.ToImmutableArray();
    }

    private static bool HasSharpPackMemberCustomFormatterAttribute(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            for (var current = attribute.AttributeClass;
                 current is not null;
                 current = current.BaseType)
            {
                if (!string.Equals(
                        current.ContainingNamespace?.ToDisplayString(),
                        "SharpPack",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(
                        current.MetadataName,
                        "SharpPackCustomFormatterAttribute`1",
                        StringComparison.Ordinal) ||
                    string.Equals(
                        current.MetadataName,
                        "SharpPackCustomFormatterAttribute`2",
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
