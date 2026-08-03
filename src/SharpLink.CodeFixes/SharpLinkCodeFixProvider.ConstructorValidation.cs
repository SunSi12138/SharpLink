namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static bool CanExposePublicParameterlessConstructor(INamedTypeSymbol type)
    {
        if (GetPublicParameterlessCallTarget(type) is not null)
            return true;
        var callableConstructors = type.InstanceConstructors
            .Where(CanInvokeWithoutArguments)
            .ToArray();
        if (callableConstructors.Length != 0)
        {
            return callableConstructors.Any(static constructor =>
                !IsObsoleteWithError(constructor) &&
                constructor.Parameters.Length == 0);
        }
        var baseType = type.BaseType;
        if (baseType is null)
            return true;
        return baseType.InstanceConstructors.Any(constructor =>
            CanInvokeWithoutArguments(constructor) &&
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
        var constructor = GetPublicParameterlessCallTarget(type) ??
                          type.InstanceConstructors.FirstOrDefault(static candidate =>
                              candidate.Parameters.Length == 0 && !IsObsoleteWithError(candidate));
        return constructor is not null && HasAttribute(
            constructor,
            "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");
    }

    private static IMethodSymbol? GetPublicParameterlessCallTarget(INamedTypeSymbol type)
    {
        var constructors = type.InstanceConstructors.Where(static constructor =>
            constructor.DeclaredAccessibility == Accessibility.Public &&
            CanInvokeWithoutArguments(constructor)).ToArray();
        var parameterless = constructors.FirstOrDefault(static constructor => constructor.Parameters.Length == 0);
        var selected = parameterless ?? (constructors.Length == 1 ? constructors[0] : null);
        return selected is not null && !IsObsoleteWithError(selected) ? selected : null;
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
        => !type.InstanceConstructors.Any(CanInvokeWithoutArguments) &&
           type.DeclaringSyntaxReferences.Any(reference =>
               reference.GetSyntax(cancellationToken) is ClassDeclarationSyntax
               {
                   ParameterList.Parameters.Count: > 0
               });

    private static bool CanInvokeWithoutArguments(IMethodSymbol constructor)
        => constructor.Parameters.All(static parameter => parameter.IsOptional || parameter.IsParams);
}
