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

    private static bool CanExposePublicParameterlessConstructor(INamedTypeSymbol type)
    {
        var declaredParameterless = type.InstanceConstructors
            .Where(static constructor => constructor.Parameters.Length == 0)
            .ToArray();
        if (declaredParameterless.Length != 0)
            return declaredParameterless.Any(static constructor => !IsObsoleteWithError(constructor));
        var baseType = type.BaseType;
        if (baseType is null)
            return true;
        return baseType.InstanceConstructors.Any(constructor =>
            constructor.Parameters.Length == 0 &&
            !IsObsoleteWithError(constructor) &&
            (constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or
                 Accessibility.ProtectedOrInternal ||
             constructor.DeclaredAccessibility == Accessibility.Internal &&
             SymbolEqualityComparer.Default.Equals(
                 constructor.ContainingAssembly, type.ContainingAssembly)));
    }

    private static bool CanCallParameterlessConstructorWithRequiredMembers(INamedTypeSymbol type)
    {
        if (!HasRequiredMembers(type))
            return true;
        var constructor = type.InstanceConstructors.FirstOrDefault(static candidate =>
            candidate.Parameters.Length == 0 && !candidate.IsStatic && !IsObsoleteWithError(candidate));
        return constructor is not null && HasAttribute(
            constructor,
            "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");
    }

    private static bool HasRequiredMembers(INamedTypeSymbol type)
        => new[] { type }.Concat(GetBaseTypes(type))
            .SelectMany(static current => current.GetMembers())
            .Any(static member => member is IFieldSymbol { IsRequired: true } or
                IPropertySymbol { IsRequired: true });

    private static bool ConstructorSatisfiesRequiredMembers(
        INamedTypeSymbol type,
        IMethodSymbol constructor)
        => !HasRequiredMembers(type) ||
           HasAttribute(
               constructor,
               "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");

    private static bool CanApplyConstructorSelectionAttribute(
        IMethodSymbol constructor,
        CancellationToken cancellationToken)
        => constructor.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax(cancellationToken).AncestorsAndSelf().Any(static syntax =>
                syntax is ConstructorDeclarationSyntax or RecordDeclarationSyntax));

    private static bool HasMembersIncompatibleWithSealing(
        INamedTypeSymbol type,
        bool allowParameterlessConstructorPublicization = false)
        => type.GetMembers().Any(member =>
            !member.IsImplicitlyDeclared &&
            !(allowParameterlessConstructorPublicization && member is IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters.Length: 0
            }) &&
            (member.IsVirtual && !member.IsOverride ||
             !member.IsOverride && member.DeclaredAccessibility is
                 Accessibility.Protected or
                 Accessibility.ProtectedOrInternal or
                 Accessibility.ProtectedAndInternal));

    private static bool HasPrimaryConstructorWithoutParameterlessAlternative(
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
        => !type.InstanceConstructors.Any(static constructor => constructor.Parameters.Length == 0) &&
           type.DeclaringSyntaxReferences.Any(reference =>
               reference.GetSyntax(cancellationToken) is ClassDeclarationSyntax
               {
                   ParameterList.Parameters.Count: > 0
               });

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
            IsAccessibleAtServiceVisibility(parameter.Type, requirePublicDependencies));
    }

    private static bool IsAccessibleAtServiceVisibility(ITypeSymbol type, bool requirePublic)
        => type switch
        {
            IArrayTypeSymbol array => IsAccessibleAtServiceVisibility(array.ElementType, requirePublic),
            IPointerTypeSymbol => false,
            IFunctionPointerTypeSymbol => false,
            ITypeParameterSymbol => false,
            IErrorTypeSymbol => false,
            INamedTypeSymbol named =>
                (requirePublic ? IsEffectivelyPublic(named.OriginalDefinition) : IsAccessibleFromGeneratedCode(named)) &&
                (named.ContainingType is null ||
                 IsAccessibleAtServiceVisibility(named.ContainingType, requirePublic)) &&
                named.TypeArguments.All(argument => IsAccessibleAtServiceVisibility(argument, requirePublic)),
            IDynamicTypeSymbol => true,
            _ => true
        };

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

    private static BaseTypeDeclarationSyntax MakePublic(BaseTypeDeclarationSyntax declaration)
        => declaration.WithModifiers(WithAccessibility(declaration.Modifiers, SyntaxKind.PublicKeyword));

    private static DelegateDeclarationSyntax MakePublic(DelegateDeclarationSyntax declaration)
        => declaration.WithModifiers(WithAccessibility(declaration.Modifiers, SyntaxKind.PublicKeyword));

    private static MemberDeclarationSyntax MakePublic(MemberDeclarationSyntax declaration)
        => declaration switch
        {
            BaseTypeDeclarationSyntax type => MakePublic(type),
            DelegateDeclarationSyntax @delegate => MakePublic(@delegate),
            _ => declaration
        };

    private static SyntaxTokenList WithAccessibility(SyntaxTokenList modifiers, SyntaxKind accessibility)
    {
        var updated = new SyntaxTokenList(modifiers.Where(static token =>
            token.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or
                SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword or SyntaxKind.FileKeyword)));
        return updated.Insert(0, SyntaxFactory.Token(accessibility));
    }

    private static TypeDeclarationSyntax AddModifier(TypeDeclarationSyntax declaration, SyntaxKind modifier)
        => declaration.WithModifiers(AddModifier(declaration.Modifiers, modifier));

    private static SyntaxTokenList AddModifier(SyntaxTokenList modifiers, SyntaxKind modifier)
    {
        if (modifiers.Any(modifier))
            return modifiers;
        for (var index = 0; index < modifiers.Count; index++)
        {
            if (modifiers[index].IsKind(SyntaxKind.PartialKeyword))
                return modifiers.Insert(index, SyntaxFactory.Token(modifier));
        }
        return modifiers.Add(SyntaxFactory.Token(modifier));
    }

    private static TypeDeclarationSyntax RemoveModifier(TypeDeclarationSyntax declaration, SyntaxKind modifier)
        => declaration.WithModifiers(RemoveModifier(declaration.Modifiers, modifier));

    private static SyntaxTokenList RemoveModifier(SyntaxTokenList modifiers, SyntaxKind modifier)
        => new(modifiers.Where(token => !token.IsKind(modifier)));

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
