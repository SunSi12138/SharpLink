namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static bool HasValidRpcContractShapeForAnnotation(INamedTypeSymbol contract)
    {
        if (contract.Arity > 0 ||
            GetContainingTypes(contract).Any(static containing => containing.Arity > 0) ||
            !IsEffectivelyPublic(contract) ||
            HasUnsupportedRpcContractMember(contract) ||
            HasErrorObsoleteRpcMethod(contract) ||
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

    private static bool HasErrorObsoleteRpcMethod(INamedTypeSymbol contract)
        => contract.GetMembers().OfType<IMethodSymbol>()
               .Concat(contract.AllInterfaces
                   .Where(static inherited => !IsIService(inherited))
                   .SelectMany(static inherited => inherited.GetMembers().OfType<IMethodSymbol>()))
               .Any(static method =>
                   method.MethodKind == MethodKind.Ordinary &&
                   method.DeclaredAccessibility == Accessibility.Public &&
                   IsObsoleteWithError(method));

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

}
