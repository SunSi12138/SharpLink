namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static bool IsCancellationTokenParameter(IParameterSymbol parameter)
        => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.CancellationToken";

    private static bool HasValidControlParameterOrder(IMethodSymbol method)
        => !method.Parameters.Any(IsCancellationTokenParameter) ||
           IsCancellationTokenParameter(method.Parameters[method.Parameters.Length - 1]);

    private static bool InheritsIService(INamedTypeSymbol symbol)
        => symbol.AllInterfaces.Any(IsIService);

    private static IEnumerable<IMethodSymbol> GetContractMethods(INamedTypeSymbol symbol)
    {
        var methods = new List<IMethodSymbol>();
        foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>()
                     .Where(static method => method.MethodKind == MethodKind.Ordinary &&
                                             method.DeclaredAccessibility == Accessibility.Public))
        {
            methods.Add(method);
        }

        foreach (var method in symbol.AllInterfaces
                     .Where(static contract => !IsIService(contract))
                     .OrderBy(static contract => contract.ToDisplayString(), StringComparer.Ordinal)
                     .SelectMany(static contract => contract.GetMembers()
                         .OfType<IMethodSymbol>()
                         .Where(static method => method.MethodKind == MethodKind.Ordinary &&
                                                 method.DeclaredAccessibility == Accessibility.Public)))
        {
            if (!methods.Any(existing => HasSameContractSignature(existing, method)))
                methods.Add(method);
        }

        return methods;
    }

    private static bool HasSameContractSignature(IMethodSymbol left, IMethodSymbol right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            left.Arity != right.Arity ||
            left.Parameters.Length != right.Parameters.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Parameters.Length; index++)
        {
            var leftParameter = left.Parameters[index];
            var rightParameter = right.Parameters[index];
            if (leftParameter.RefKind != rightParameter.RefKind ||
                !SymbolEqualityComparer.Default.Equals(leftParameter.Type, rightParameter.Type))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<IMethodSymbol> GetConflictingInheritedRpcSignatures(INamedTypeSymbol symbol)
    {
        if (!symbol.AllInterfaces.Any(static contract => !IsIService(contract)))
            yield break;

        var directMethods = symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary &&
                                    method.DeclaredAccessibility == Accessibility.Public)
            .ToArray();
        var methods = directMethods
            .Concat(symbol.AllInterfaces
                .Where(static contract => !IsIService(contract))
                .SelectMany(static contract => contract.GetMembers().OfType<IMethodSymbol>()))
            .Where(static method => method.MethodKind == MethodKind.Ordinary &&
                                    method.DeclaredAccessibility == Accessibility.Public)
            .ToArray();
        var groups = new List<InheritedRpcSignatureGroup>();
        for (var methodIndex = 0; methodIndex < methods.Length; methodIndex++)
        {
            var method = methods[methodIndex];
            var groupIndex = -1;
            for (var candidateIndex = 0; candidateIndex < groups.Count; candidateIndex++)
            {
                if (!HasSameContractSignature(groups[candidateIndex].Representative, method))
                    continue;
                groupIndex = candidateIndex;
                break;
            }
            if (groupIndex < 0)
            {
                var hasDirectDeclaration = methodIndex < directMethods.Length;
                groups.Add(new InheritedRpcSignatureGroup(
                    method,
                    hasDirectDeclaration ? default : GetInheritedRpcPolicy(method),
                    hasDirectDeclaration,
                    Reported: false));
                continue;
            }

            var group = groups[groupIndex];
            if (group.Reported)
                continue;
            if (SymbolEqualityComparer.IncludeNullability.Equals(
                    group.Representative.ReturnType,
                    method.ReturnType) &&
                (group.HasDirectDeclaration || HasCompatibleInheritedRpcSemantics(
                    group.Representative,
                    method,
                    group.Policy,
                    GetInheritedRpcPolicy(method))))
            {
                continue;
            }

            groups[groupIndex] = group with { Reported = true };
            yield return group.Representative;
        }
    }

    private static bool HasCompatibleInheritedRpcSemantics(
        IMethodSymbol left,
        IMethodSymbol right,
        InheritedRpcPolicy leftPolicy,
        InheritedRpcPolicy rightPolicy)
    {
        for (var index = 0; index < left.Parameters.Length; index++)
        {
            var leftParameter = left.Parameters[index];
            var rightParameter = right.Parameters[index];
            if (IsCancellationTokenParameter(leftParameter))
                continue;
            if (!string.Equals(leftParameter.Name, rightParameter.Name, StringComparison.Ordinal) ||
                !SymbolEqualityComparer.IncludeNullability.Equals(leftParameter.Type, rightParameter.Type))
            {
                return false;
            }
        }

        return leftPolicy == rightPolicy;
    }

    private static InheritedRpcPolicy GetInheritedRpcPolicy(IMethodSymbol method)
    {
        var isOneway = false;
        var isIdempotent = false;
        var isNonCancellable = false;
        var hasTimeout = false;
        long? timeoutTicks = null;
        foreach (var attribute in method.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
                continue;
            var attributeNamespace = attributeClass.ContainingNamespace;
            if (attributeNamespace.ContainingNamespace is not { Name: "SharpLink" } root ||
                !root.ContainingNamespace.IsGlobalNamespace ||
                attributeNamespace.Name is not ("Sdk" or "Abstractions"))
            {
                continue;
            }

            switch (attributeClass.Name)
            {
                case "OnewayAttribute":
                    isOneway = true;
                    break;
                case "IdempotentAttribute":
                    isIdempotent = true;
                    break;
                case "NonCancellableAttribute":
                    isNonCancellable = true;
                    break;
                case "TimeoutAttribute":
                    hasTimeout = true;
                    if (TryGetTimeoutSeconds(attribute, out var seconds) &&
                        TryNormalizeTimeoutSeconds(seconds, out var ticks, out _))
                    {
                        timeoutTicks = ticks;
                    }
                    break;
            }
        }
        return new InheritedRpcPolicy(
            isOneway,
            isIdempotent,
            isNonCancellable,
            hasTimeout,
            timeoutTicks);
    }

    private readonly record struct InheritedRpcPolicy(
        bool IsOneway,
        bool IsIdempotent,
        bool IsNonCancellable,
        bool HasTimeout,
        long? TimeoutTicks);

    private readonly record struct InheritedRpcSignatureGroup(
        IMethodSymbol Representative,
        InheritedRpcPolicy Policy,
        bool HasDirectDeclaration,
        bool Reported);

    private static bool IsIService(INamedTypeSymbol symbol)
        => string.Equals(symbol.Name, "IService", StringComparison.Ordinal) &&
           string.Equals(symbol.ContainingNamespace.ToDisplayString(), "SharpLink.Sdk", StringComparison.Ordinal);

    private static bool IsRpcServiceAttribute(AttributeData attribute)
        => IsAttribute(attribute, "SharpLink.Sdk", "RpcServiceAttribute") ||
           IsAttribute(attribute, "SharpLink.Abstractions", "RpcServiceAttribute");

    private static bool IsOnewayAttribute(AttributeData attribute)
        => IsAttribute(attribute, "SharpLink.Sdk", "OnewayAttribute") ||
           IsAttribute(attribute, "SharpLink.Abstractions", "OnewayAttribute");

    private static bool IsTimeoutAttribute(AttributeData attribute)
        => IsAttribute(attribute, "SharpLink.Sdk", "TimeoutAttribute") ||
           IsAttribute(attribute, "SharpLink.Abstractions", "TimeoutAttribute");

    private static bool IsIdempotentAttribute(AttributeData attribute)
        => IsAttribute(attribute, "SharpLink.Sdk", "IdempotentAttribute") ||
           IsAttribute(attribute, "SharpLink.Abstractions", "IdempotentAttribute");

    private static bool IsNonCancellableAttribute(AttributeData attribute)
        => IsAttribute(attribute, "SharpLink.Sdk", "NonCancellableAttribute") ||
           IsAttribute(attribute, "SharpLink.Abstractions", "NonCancellableAttribute");

    private static long? GetTimeoutTicksOrNull(IMethodSymbol method, out bool hasTimeoutAttribute)
    {
        hasTimeoutAttribute = false;
        foreach (var attribute in method.GetAttributes())
        {
            if (!IsTimeoutAttribute(attribute))
                continue;

            hasTimeoutAttribute = true;
            if (attribute.ConstructorArguments.Length == 0)
                return null;

            return TryGetTimeoutSeconds(attribute, out var seconds) &&
                   TryNormalizeTimeoutSeconds(seconds, out var ticks, out _)
                ? ticks
                : null;
        }

        return null;
    }

    private static bool TryGetTimeoutSeconds(AttributeData attribute, out double seconds)
    {
        seconds = default;
        if (attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is null)
            return false;

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

    private static bool TryValidateTimeoutSeconds(double seconds, out string detail)
        => TryNormalizeTimeoutSeconds(seconds, out _, out detail);

    private static bool TryNormalizeTimeoutSeconds(double seconds, out long ticks, out string detail)
    {
        ticks = default;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
        {
            detail = "seconds must be a finite number greater than zero";
            return false;
        }

        try
        {
            var timeout = TimeSpan.FromSeconds(seconds);
            if (timeout <= TimeSpan.Zero)
            {
                detail = "seconds is too small to produce a positive TimeSpan";
                return false;
            }
            ticks = timeout.Ticks;
        }
        catch (OverflowException)
        {
            detail = "seconds exceeds the supported TimeSpan range";
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            detail = "seconds exceeds the supported TimeSpan range";
            return false;
        }

        detail = string.Empty;
        return true;
    }
}
