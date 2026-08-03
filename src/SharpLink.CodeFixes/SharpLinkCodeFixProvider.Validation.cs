namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static bool HasValidRpcContractShapeForAnnotation(INamedTypeSymbol contract)
    {
        if (contract.Arity > 0 ||
            GetContainingTypes(contract).Any(static containing => containing.Arity > 0) ||
            !IsEffectivelyPublic(contract) ||
            HasUnsupportedRpcContractMember(contract) ||
            HasConflictingInheritedRpcSignatures(contract))
        {
            return false;
        }

        return GetRpcContractMethods(contract).All(static method =>
            IsSupportedRpcReturnType(method.ReturnType) &&
            !method.IsStatic &&
            !method.ReturnsByRef &&
            !method.ReturnsByRefReadonly &&
            method.Parameters.All(static parameter => parameter.RefKind == RefKind.None) &&
            !ContainsRefLikeType(method.ReturnType) &&
            method.Parameters.All(static parameter => !ContainsRefLikeType(parameter.Type)) &&
            !ContainsContractPointerOrFunctionPointer(method.ReturnType) &&
            method.Parameters.All(static parameter =>
                !ContainsContractPointerOrFunctionPointer(parameter.Type)) &&
            !method.IsGenericMethod &&
            !ContainsContractTypeParameter(method.ReturnType) &&
            method.Parameters.All(static parameter => !ContainsContractTypeParameter(parameter.Type)) &&
            method.Parameters.Count(static parameter =>
                IsControlParameter(parameter, ControlParameterKind.CancellationToken)) <= 1 &&
            method.Parameters.Count(static parameter =>
                IsControlParameter(parameter, ControlParameterKind.CallOptions)) <= 1 &&
            HasValidRpcControlParameterOrder(method) &&
            HasValidRpcCancellationPolicy(method) &&
            method.Parameters.Count(static parameter => IsAsyncEnumerableType(parameter.Type)) <= sbyte.MaxValue &&
            !HasInvalidRpcMethodAttributes(method));
    }

    private static bool HasUnsupportedRpcContractMember(INamedTypeSymbol contract)
        => HasUnsupportedRpcContractMemberDirect(contract) ||
           contract.AllInterfaces.Any(static inherited =>
               !IsIService(inherited) && HasUnsupportedRpcContractMemberDirect(inherited));

    private static bool HasUnsupportedRpcContractMemberDirect(INamedTypeSymbol contract)
        => contract.GetMembers().Any(static member =>
            member is IPropertySymbol { IsAbstract: true } or IEventSymbol { IsAbstract: true } ||
            member is IMethodSymbol
            {
                IsAbstract: true,
                MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion
            } ||
            member is IMethodSymbol
            {
                MethodKind: MethodKind.Ordinary,
                IsAbstract: true,
                DeclaredAccessibility: not Accessibility.Public
            });

    private static IEnumerable<IMethodSymbol> GetRpcContractMethods(INamedTypeSymbol contract)
    {
        var methods = new List<IMethodSymbol>();
        foreach (var method in contract.GetMembers().OfType<IMethodSymbol>().Where(static method =>
                     method.MethodKind == MethodKind.Ordinary &&
                     method.DeclaredAccessibility == Accessibility.Public))
        {
            methods.Add(method);
        }

        foreach (var method in contract.AllInterfaces
                     .Where(static inherited => !IsIService(inherited))
                     .OrderBy(static inherited => inherited.ToDisplayString(), StringComparer.Ordinal)
                     .SelectMany(static inherited => inherited.GetMembers().OfType<IMethodSymbol>())
                     .Where(static method => method.MethodKind == MethodKind.Ordinary &&
                                             method.DeclaredAccessibility == Accessibility.Public))
        {
            if (!methods.Any(existing => HasEquivalentContractSignature(existing, method)))
                methods.Add(method);
        }
        return methods;
    }

    private static bool HasConflictingInheritedRpcSignatures(INamedTypeSymbol contract)
    {
        if (!contract.AllInterfaces.Any(static inherited => !IsIService(inherited)))
            return false;

        var directMethods = contract.GetMembers().OfType<IMethodSymbol>().Where(static method =>
            method.MethodKind == MethodKind.Ordinary &&
            method.DeclaredAccessibility == Accessibility.Public).ToArray();
        var methods = directMethods.Concat(contract.AllInterfaces
                .Where(static inherited => !IsIService(inherited))
                .SelectMany(static inherited => inherited.GetMembers().OfType<IMethodSymbol>()))
            .Where(static method => method.MethodKind == MethodKind.Ordinary &&
                                    method.DeclaredAccessibility == Accessibility.Public)
            .ToArray();
        var groups = new List<(IMethodSymbol Representative,
            (bool Oneway, bool Idempotent, bool NonCancellable, bool HasTimeout, double? Timeout) Policy,
            bool HasDirectDeclaration)>();
        for (var index = 0; index < methods.Length; index++)
        {
            var method = methods[index];
            var groupIndex = groups.FindIndex(group =>
                HasEquivalentContractSignature(group.Representative, method));
            if (groupIndex < 0)
            {
                var hasDirectDeclaration = index < directMethods.Length;
                groups.Add((
                    method,
                    hasDirectDeclaration ? default : GetInheritedRpcPolicy(method),
                    hasDirectDeclaration));
                continue;
            }

            var group = groups[groupIndex];
            if (!SymbolEqualityComparer.IncludeNullability.Equals(
                    group.Representative.ReturnType, method.ReturnType) ||
                !group.HasDirectDeclaration && !HasCompatibleInheritedRpcSemantics(
                    group.Representative,
                    method,
                    group.Policy,
                    GetInheritedRpcPolicy(method)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasCompatibleInheritedRpcSemantics(
        IMethodSymbol left,
        IMethodSymbol right,
        (bool Oneway, bool Idempotent, bool NonCancellable, bool HasTimeout, double? Timeout) leftPolicy,
        (bool Oneway, bool Idempotent, bool NonCancellable, bool HasTimeout, double? Timeout) rightPolicy)
    {
        for (var index = 0; index < left.Parameters.Length; index++)
        {
            var leftParameter = left.Parameters[index];
            var rightParameter = right.Parameters[index];
            if (IsControlParameter(leftParameter, ControlParameterKind.CancellationToken) ||
                IsControlParameter(leftParameter, ControlParameterKind.CallOptions))
            {
                continue;
            }
            if (!string.Equals(leftParameter.Name, rightParameter.Name, StringComparison.Ordinal) ||
                !SymbolEqualityComparer.IncludeNullability.Equals(leftParameter.Type, rightParameter.Type))
            {
                return false;
            }
        }
        return leftPolicy == rightPolicy;
    }

    private static (bool Oneway, bool Idempotent, bool NonCancellable, bool HasTimeout, double? Timeout)
        GetInheritedRpcPolicy(IMethodSymbol method)
    {
        var oneway = false;
        var idempotent = false;
        var nonCancellable = false;
        var hasTimeout = false;
        double? timeout = null;
        foreach (var attribute in method.GetAttributes())
        {
            var metadataName = attribute.AttributeClass?.ToDisplayString();
            switch (metadataName)
            {
                case "SharpLink.Sdk.OnewayAttribute":
                case "SharpLink.Abstractions.OnewayAttribute":
                    oneway = true;
                    break;
                case "SharpLink.Sdk.IdempotentAttribute":
                case "SharpLink.Abstractions.IdempotentAttribute":
                    idempotent = true;
                    break;
                case "SharpLink.Sdk.NonCancellableAttribute":
                case "SharpLink.Abstractions.NonCancellableAttribute":
                    nonCancellable = true;
                    break;
                case "SharpLink.Sdk.TimeoutAttribute":
                case "SharpLink.Abstractions.TimeoutAttribute":
                    hasTimeout = true;
                    if (TryGetTimeoutSeconds(attribute, out var seconds))
                        timeout = seconds;
                    break;
            }
        }
        return (oneway, idempotent, nonCancellable, hasTimeout, timeout);
    }

    private static bool IsSupportedRpcReturnType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return false;
        var original = named.OriginalDefinition;
        var @namespace = original.ContainingNamespace.ToDisplayString();
        return @namespace == "System.Threading.Tasks" &&
               original is { Name: "Task", Arity: 0 or 1 } or
                   { Name: "ValueTask", Arity: 0 or 1 } ||
               @namespace == "System.Collections.Generic" &&
               original is { Name: "IAsyncEnumerable", Arity: 1 };
    }

    private static bool ContainsContractTypeParameter(ITypeSymbol type)
        => type.TypeKind == TypeKind.TypeParameter ||
           type switch
           {
               IArrayTypeSymbol array => ContainsContractTypeParameter(array.ElementType),
               IPointerTypeSymbol pointer => ContainsContractTypeParameter(pointer.PointedAtType),
               INamedTypeSymbol named => named.IsUnboundGenericType ||
                                        named.TypeArguments.Any(ContainsContractTypeParameter),
               _ => false
           };

    private static bool ContainsRefLikeType(ITypeSymbol type)
        => type switch
        {
            INamedTypeSymbol { IsRefLikeType: true } => true,
            IArrayTypeSymbol array => ContainsRefLikeType(array.ElementType),
            IPointerTypeSymbol pointer => ContainsRefLikeType(pointer.PointedAtType),
            INamedTypeSymbol named => named.TypeArguments.Any(ContainsRefLikeType),
            _ => false
        };

    private static bool ContainsContractPointerOrFunctionPointer(ITypeSymbol type)
        => type switch
        {
            IPointerTypeSymbol or IFunctionPointerTypeSymbol => true,
            IArrayTypeSymbol array => ContainsContractPointerOrFunctionPointer(array.ElementType),
            INamedTypeSymbol named => named.TypeArguments.Any(ContainsContractPointerOrFunctionPointer),
            _ => false
        };

    private static bool HasValidRpcControlParameterOrder(IMethodSymbol method)
    {
        var controls = method.Parameters.Where(static parameter =>
            IsControlParameter(parameter, ControlParameterKind.CancellationToken) ||
            IsControlParameter(parameter, ControlParameterKind.CallOptions)).ToArray();
        if (controls.Length == 0)
            return true;
        var firstControl = method.Parameters.Length - controls.Length;
        for (var index = firstControl; index < method.Parameters.Length; index++)
        {
            if (!IsControlParameter(method.Parameters[index], ControlParameterKind.CancellationToken) &&
                !IsControlParameter(method.Parameters[index], ControlParameterKind.CallOptions))
            {
                return false;
            }
        }
        return !method.Parameters.Any(static parameter =>
                   IsControlParameter(parameter, ControlParameterKind.CancellationToken)) ||
               IsControlParameter(method.Parameters[method.Parameters.Length - 1],
                   ControlParameterKind.CancellationToken);
    }

    private static bool HasValidRpcCancellationPolicy(IMethodSymbol method)
        => method.Parameters.Any(static parameter =>
               IsControlParameter(parameter, ControlParameterKind.CancellationToken)) !=
           method.GetAttributes().Any(static attribute => string.Equals(
                   attribute.AttributeClass?.ToDisplayString(),
                   "SharpLink.Sdk.NonCancellableAttribute",
                   StringComparison.Ordinal) ||
               string.Equals(
                   attribute.AttributeClass?.ToDisplayString(),
                   "SharpLink.Abstractions.NonCancellableAttribute",
                   StringComparison.Ordinal));

    private static bool IsAsyncEnumerableType(ITypeSymbol type)
        => type is INamedTypeSymbol named &&
           named.OriginalDefinition is { Name: "IAsyncEnumerable", Arity: 1 } &&
           named.OriginalDefinition.ContainingNamespace.ToDisplayString() ==
           "System.Collections.Generic";

    private static bool HasInvalidRpcMethodAttributes(IMethodSymbol method)
    {
        var oneway = false;
        foreach (var attribute in method.GetAttributes())
        {
            if (IsOnewayAttribute(attribute))
            {
                oneway = true;
            }
            else if (IsTimeoutAttribute(attribute) &&
                     TryGetTimeoutSeconds(attribute, out var seconds) &&
                     !IsValidTimeoutSeconds(seconds))
            {
                return true;
            }
        }
        return oneway && !IsValidOnewayReturnType(method.ReturnType);
    }

    private static bool IsValidOnewayReturnType(ITypeSymbol type)
        => type is INamedTypeSymbol { Arity: 0 } named &&
           named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
           named.Name is "Task" or "ValueTask";

    private static bool TryGetTimeoutSeconds(AttributeData attribute, out double seconds)
    {
        seconds = default;
        if (attribute.ConstructorArguments.Length == 0 ||
            attribute.ConstructorArguments[0].Value is null)
        {
            return false;
        }
        switch (attribute.ConstructorArguments[0].Value)
        {
            case double value:
                seconds = value;
                return true;
            case float value:
                seconds = value;
                return true;
            case int value:
                seconds = value;
                return true;
            case long value:
                seconds = value;
                return true;
            default:
                return false;
        }
    }

    private static bool IsValidTimeoutSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
            return false;
        try
        {
            return TimeSpan.FromSeconds(seconds) > TimeSpan.Zero;
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

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

    private static bool HasMembersIncompatibleWithSealing(INamedTypeSymbol type)
        => type.GetMembers().Any(static member =>
            !member.IsImplicitlyDeclared &&
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
        => symbol.GetAttributes().Any(static attribute =>
            string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                "System.ObsoleteAttribute",
                StringComparison.Ordinal) &&
            attribute.ConstructorArguments.Length > 1 &&
            attribute.ConstructorArguments[1].Value is true);

    private static bool CanExposeAsPublic(IMethodSymbol constructor)
        => constructor.Parameters.All(static parameter => IsPubliclyAccessible(parameter.Type));

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
        var attribute = service.GetAttributes().FirstOrDefault(static candidate => string.Equals(
            candidate.AttributeClass?.ToDisplayString(),
            "SharpLink.Sdk.RpcServiceAttribute",
            StringComparison.Ordinal));
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
