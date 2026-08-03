namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static bool CanApplyConstructorSelectionMarker(
        INamedTypeSymbol marker,
        SemanticModel semanticModel,
        int position)
    {
        if (marker.TypeKind != TypeKind.Class || marker.IsAbstract || marker.IsGenericType ||
            IsObsoleteWithError(marker) ||
            !semanticModel.IsAccessible(position, marker) ||
            !InheritsSystemAttribute(marker) ||
            !CanTargetConstructors(marker))
        {
            return false;
        }

        return marker.InstanceConstructors.Any(constructor =>
            !constructor.IsStatic &&
            constructor.Parameters.Length == 0 &&
            !IsObsoleteWithError(constructor) &&
            semanticModel.IsAccessible(position, constructor));
    }

    private static bool InheritsSystemAttribute(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.ToDisplayString(), "System.Attribute", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool CanTargetConstructors(INamedTypeSymbol marker)
    {
        var usage = marker.GetAttributes().FirstOrDefault(static attribute => string.Equals(
            attribute.AttributeClass?.ToDisplayString(),
            "System.AttributeUsageAttribute",
            StringComparison.Ordinal));
        if (usage is null)
            return true;
        if (usage.ConstructorArguments.Length != 1 ||
            usage.ConstructorArguments[0].Value is not int targets)
        {
            return false;
        }
        return (targets & (int)AttributeTargets.Constructor) != 0;
    }
}
