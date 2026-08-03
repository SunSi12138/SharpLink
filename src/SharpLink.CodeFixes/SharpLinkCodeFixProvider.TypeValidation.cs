namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static bool IsSafeToMakeConcrete(INamedTypeSymbol type)
    {
        if (type.GetMembers().Any(static member => member.IsAbstract && !member.IsImplicitlyDeclared))
            return false;
        foreach (var @interface in type.AllInterfaces)
        {
            foreach (var member in @interface.GetMembers())
            {
                if (type.FindImplementationForInterfaceMember(member) is null)
                    return false;
            }
        }
        foreach (var baseType in GetBaseTypes(type))
        {
            foreach (var member in baseType.GetMembers().Where(static member => member.IsAbstract))
            {
                if (!HasOverride(type, member))
                    return false;
            }
        }
        return true;
    }

    private static IEnumerable<INamedTypeSymbol> GetBaseTypes(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            yield return current;
    }

    private static IEnumerable<INamedTypeSymbol> GetContainingTypes(INamedTypeSymbol type)
    {
        for (var current = type.ContainingType; current is not null; current = current.ContainingType)
            yield return current;
    }

    private static bool HasOverride(INamedTypeSymbol type, ISymbol abstractMember)
    {
        foreach (var currentType in new[] { type }.Concat(GetBaseTypes(type)))
        {
            var overrides = currentType.GetMembers(abstractMember.Name)
                .Where(candidate => Overrides(candidate, abstractMember))
                .ToArray();
            if (overrides.Length != 0)
                return overrides.Any(static candidate => !candidate.IsAbstract);
        }
        return false;

        static bool Overrides(ISymbol candidate, ISymbol abstractMember)
        {
            for (var current = candidate; current is not null; current = GetOverriddenMember(current))
            {
                if (SymbolEqualityComparer.Default.Equals(current, abstractMember))
                    return true;
            }
            return false;
        }
    }

    private static ISymbol? GetOverriddenMember(ISymbol symbol)
        => symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null
        };

    private static bool IsSupportedServiceConstructor(IMethodSymbol constructor)
        => !IsObsoleteWithError(constructor) &&
           constructor.Parameters.All(static parameter =>
            parameter.RefKind is not (RefKind.Ref or RefKind.Out or RefKind.RefReadOnlyParameter) &&
            parameter.Type.TypeKind is not (TypeKind.Pointer or TypeKind.FunctionPointer) &&
            !ContainsRefLikeType(parameter.Type));

    private static bool IsObsoleteWithError(ISymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.GetAttributes().Any(static attribute =>
                    string.Equals(
                        attribute.AttributeClass?.ToDisplayString(),
                        "System.ObsoleteAttribute",
                        StringComparison.Ordinal) &&
                    attribute.ConstructorArguments.Length > 1 &&
                    attribute.ConstructorArguments[1].Value is true))
            {
                return true;
            }
        }
        return false;
    }

    private static bool CanExposeAsPublic(IMethodSymbol constructor)
    {
        var requirePublicDependencies = IsEffectivelyPublic(constructor.ContainingType);
        return constructor.Parameters.All(parameter =>
            IsAccessibleAtServiceVisibility(
                parameter.Type,
                requirePublicDependencies,
                constructor.ContainingAssembly));
    }

    private static bool IsAccessibleAtServiceVisibility(
        ITypeSymbol type,
        bool requirePublic,
        IAssemblySymbol serviceAssembly)
        => type switch
        {
            IArrayTypeSymbol array => IsAccessibleAtServiceVisibility(
                array.ElementType, requirePublic, serviceAssembly),
            IPointerTypeSymbol => false,
            IFunctionPointerTypeSymbol => false,
            ITypeParameterSymbol => false,
            IErrorTypeSymbol => false,
            INamedTypeSymbol named =>
                (requirePublic
                    ? IsEffectivelyPublic(named.OriginalDefinition)
                    : IsAccessibleFromAssembly(named.OriginalDefinition, serviceAssembly)) &&
                (named.ContainingType is null || IsAccessibleAtServiceVisibility(
                    named.ContainingType, requirePublic, serviceAssembly)) &&
                named.TypeArguments.All(argument => IsAccessibleAtServiceVisibility(
                    argument, requirePublic, serviceAssembly)),
            IDynamicTypeSymbol => true,
            _ => true
        };

    private static bool IsAccessibleFromAssembly(
        INamedTypeSymbol type,
        IAssemblySymbol serviceAssembly)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            switch (current.DeclaredAccessibility)
            {
                case Accessibility.Public:
                    continue;
                case Accessibility.Internal:
                case Accessibility.ProtectedOrInternal:
                    if (current.ContainingAssembly is { } containingAssembly &&
                        (SymbolEqualityComparer.Default.Equals(containingAssembly, serviceAssembly) ||
                         containingAssembly.GivesAccessTo(serviceAssembly)))
                    {
                        continue;
                    }
                    return false;
                default:
                    return false;
            }
        }
        return true;
    }

    private static bool IsPubliclyAccessible(ITypeSymbol type)
        => type switch
        {
            IArrayTypeSymbol array => IsPubliclyAccessible(array.ElementType),
            IPointerTypeSymbol => false,
            IFunctionPointerTypeSymbol => false,
            ITypeParameterSymbol => false,
            IErrorTypeSymbol => false,
            INamedTypeSymbol named => IsEffectivelyPublic(named.OriginalDefinition) &&
                                     (named.ContainingType is null ||
                                      IsPubliclyAccessible(named.ContainingType)) &&
                                     named.TypeArguments.All(IsPubliclyAccessible),
            IDynamicTypeSymbol => true,
            _ => true
        };

    private static bool HasValidServiceActivationShape(INamedTypeSymbol service)
    {
        if (IsObsoleteWithError(service))
            return false;
        var constructors = service.InstanceConstructors
            .Where(static item => item.DeclaredAccessibility == Accessibility.Public)
            .ToArray();
        var marked = constructors.Where(static constructor => constructor.GetAttributes().Any(attribute =>
            string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute",
                StringComparison.Ordinal))).ToArray();
        var selected = marked.Length == 1
            ? marked[0]
            : marked.Length == 0 && constructors.Length == 1 ? constructors[0] : null;
        return selected is not null && IsSupportedServiceConstructor(selected) &&
               ConstructorSatisfiesRequiredMembers(service, selected);
    }

    private static bool HasValidServiceActivationShapeAfterMakingConcrete(INamedTypeSymbol service)
    {
        if (service.InstanceConstructors.All(static constructor => constructor.IsImplicitlyDeclared) &&
            service.InstanceConstructors.Any(static constructor => constructor.Parameters.Length == 0))
        {
            return !IsObsoleteWithError(service) && !HasRequiredMembers(service);
        }
        return HasValidServiceActivationShape(service);
    }

    private static bool HasValidServiceShapeAfterContractAnnotation(INamedTypeSymbol service)
        => service.TypeKind == TypeKind.Class &&
           !service.IsAbstract &&
           !service.IsGenericType &&
           IsAccessibleFromGeneratedCode(service) &&
           HasValidServiceLifetime(service) &&
           HasValidServiceActivationShape(service);

    private static bool HasValidServiceLifetime(INamedTypeSymbol service)
    {
        var attribute = service.GetAttributes().FirstOrDefault(IsRpcServiceAttribute);
        if (attribute is null)
            return true;
        foreach (var argument in attribute.NamedArguments)
        {
            if (string.Equals(argument.Key, "Lifetime", StringComparison.Ordinal) &&
                argument.Value.Value is { } value)
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture) is >= 0 and <= 2;
            }
        }
        return true;
    }

    private static bool IsAccessibleFromGeneratedCode(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsFileLocal ||
                current.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or
                    Accessibility.ProtectedAndInternal)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryGetEnumUnderlyingTypeSyntax(string type, out TypeSyntax syntax)
    {
        var keyword = type switch
        {
            "System.SByte" or "sbyte" => "sbyte",
            "System.Byte" or "byte" => "byte",
            "System.Int16" or "short" => "short",
            "System.UInt16" or "ushort" => "ushort",
            "System.Int32" or "int" => "int",
            "System.UInt32" or "uint" => "uint",
            "System.Int64" or "long" => "long",
            "System.UInt64" or "ulong" => "ulong",
            _ => string.Empty
        };
        syntax = SyntaxFactory.ParseTypeName(keyword);
        return keyword.Length != 0;
    }

    private static bool TryGetEnumUnderlyingSpecialType(string type, out SpecialType specialType)
    {
        specialType = type switch
        {
            "System.SByte" or "sbyte" => SpecialType.System_SByte,
            "System.Byte" or "byte" => SpecialType.System_Byte,
            "System.Int16" or "short" => SpecialType.System_Int16,
            "System.UInt16" or "ushort" => SpecialType.System_UInt16,
            "System.Int32" or "int" => SpecialType.System_Int32,
            "System.UInt32" or "uint" => SpecialType.System_UInt32,
            "System.Int64" or "long" => SpecialType.System_Int64,
            "System.UInt64" or "ulong" => SpecialType.System_UInt64,
            _ => SpecialType.None
        };
        return specialType != SpecialType.None;
    }

    private static bool TryGetEnumUnderlyingTypeRange(
        string type,
        out decimal minimum,
        out decimal maximum)
    {
        switch (type)
        {
            case "System.SByte":
            case "sbyte":
                minimum = sbyte.MinValue;
                maximum = sbyte.MaxValue;
                return true;
            case "System.Byte":
            case "byte":
                minimum = byte.MinValue;
                maximum = byte.MaxValue;
                return true;
            case "System.Int16":
            case "short":
                minimum = short.MinValue;
                maximum = short.MaxValue;
                return true;
            case "System.UInt16":
            case "ushort":
                minimum = ushort.MinValue;
                maximum = ushort.MaxValue;
                return true;
            case "System.Int32":
            case "int":
                minimum = int.MinValue;
                maximum = int.MaxValue;
                return true;
            case "System.UInt32":
            case "uint":
                minimum = uint.MinValue;
                maximum = uint.MaxValue;
                return true;
            case "System.Int64":
            case "long":
                minimum = long.MinValue;
                maximum = long.MaxValue;
                return true;
            case "System.UInt64":
            case "ulong":
                minimum = ulong.MinValue;
                maximum = ulong.MaxValue;
                return true;
            default:
                minimum = 0;
                maximum = 0;
                return false;
        }
    }
}
