namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static ImmutableArray<InvalidRpcMethodModel> GetInvalidRpcMethodDiagnostics(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return ImmutableArray<InvalidRpcMethodModel>.Empty;
        if (!InheritsIService(symbol))
            return ImmutableArray<InvalidRpcMethodModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidRpcMethodModel>();
        foreach (var method in GetConflictingInheritedRpcSignatures(symbol))
        {
            list.Add(new InvalidRpcMethodModel(
                InvalidRpcMethodKind.InheritedSignatureConflict,
                method.Name,
                "inherited declarations with the same CLR signature must agree on return type, call shape, execution policy, and request schema",
                method.Locations.FirstOrDefault() ?? symbol.Locations.FirstOrDefault()));
        }
        foreach (var method in GetContractMethods(symbol))
        {
            var isOneWay = false;
            if (method.IsStatic)
            {
                list.Add(new InvalidRpcMethodModel(
                    InvalidRpcMethodKind.Static,
                    method.Name,
                    "RPC routes must be instance methods",
                    method.Locations.FirstOrDefault()));
            }
            if (HasByReferenceSignature(method))
            {
                list.Add(new InvalidRpcMethodModel(
                    InvalidRpcMethodKind.ByReference,
                    method.Name,
                    "ref, ref readonly, in, and out values have no supported RPC wire model",
                    method.Locations.FirstOrDefault()));
            }
            if (!IsSupportedRpcReturnType(method.ReturnType))
            {
                list.Add(new InvalidRpcMethodModel(
                    InvalidRpcMethodKind.ReturnType,
                    method.Name,
                    method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    method.Locations.FirstOrDefault()));
            }
            foreach (var attribute in method.GetAttributes())
            {
                if (IsOnewayAttribute(attribute))
                    isOneWay = true;
                if (!IsTimeoutAttribute(attribute))
                    continue;
                if (TryGetTimeoutSeconds(attribute, out var seconds) &&
                    !TryValidateTimeoutSeconds(seconds, out var detail))
                {
                    list.Add(new InvalidRpcMethodModel(
                        InvalidRpcMethodKind.Timeout,
                        method.Name,
                        detail,
                        attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? method.Locations.FirstOrDefault()));
                }
            }
            if (isOneWay && !IsValidOnewayReturnType(method.ReturnType))
            {
                list.Add(new InvalidRpcMethodModel(
                    InvalidRpcMethodKind.OnewayReturn,
                    method.Name,
                    "only non-generic Task or ValueTask returns are supported",
                    method.Locations.FirstOrDefault()));
            }
        }

        return list.ToImmutable();
    }

    private static ImmutableArray<InvalidCancellationTokenMethodModel> GetInvalidCancellationTokenMethods(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return ImmutableArray<InvalidCancellationTokenMethodModel>.Empty;
        if (!InheritsIService(symbol))
            return ImmutableArray<InvalidCancellationTokenMethodModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidCancellationTokenMethodModel>();
        foreach (var method in GetContractMethods(symbol))
        {
            var cancellationTokenCount = method.Parameters.Count(IsCancellationTokenParameter);
            if (cancellationTokenCount <= 1)
                continue;

            list.Add(new InvalidCancellationTokenMethodModel(
                method.Name,
                method.Locations.FirstOrDefault()));
        }

        return list.ToImmutable();
    }

    private static ImmutableArray<InvalidControlParameterOrderModel> GetInvalidControlParameterOrderMethods(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface ||
            !InheritsIService(symbol))
            return ImmutableArray<InvalidControlParameterOrderModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidControlParameterOrderModel>();
        foreach (var method in GetContractMethods(symbol))
        {
            var cancellationIndex = -1;
            for (var index = 0; index < method.Parameters.Length; index++)
            {
                if (IsCancellationTokenParameter(method.Parameters[index]))
                    cancellationIndex = index;
            }

            if (cancellationIndex < 0 || cancellationIndex == method.Parameters.Length - 1)
                continue;
            list.Add(new InvalidControlParameterOrderModel(method.Name, method.Locations.FirstOrDefault()));
        }
        return list.ToImmutable();
    }

    private static bool HasInvalidRpcMethodShape(INamedTypeSymbol interfaceSymbol)
    {
        if (GetConflictingInheritedRpcSignatures(interfaceSymbol).Any())
            return true;

        return GetContractMethods(interfaceSymbol)
            .Any(m =>
                !IsSupportedRpcReturnType(m.ReturnType) ||
                m.IsStatic ||
                HasByReferenceSignature(m) ||
                ContainsRefLikeType(m.ReturnType) ||
                m.Parameters.Any(static parameter => ContainsRefLikeType(parameter.Type)) ||
                ContainsPointerOrFunctionPointer(m.ReturnType) ||
                m.Parameters.Any(static parameter => ContainsPointerOrFunctionPointer(parameter.Type)) ||
                m.Parameters.Count(IsCancellationTokenParameter) > 1 ||
                !HasValidControlParameterOrder(m) ||
                HasInvalidMethodAttributes(m));
    }

    private static bool IsValidOnewayReturnType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { Arity: 0 } named ||
            named.ContainingNamespace.ToDisplayString() != "System.Threading.Tasks")
        {
            return false;
        }
        return named.Name is "Task" or "ValueTask";
    }

    private static bool HasByReferenceSignature(IMethodSymbol method)
        => method.ReturnsByRef || method.ReturnsByRefReadonly ||
           method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None);

    private static bool HasInvalidMethodAttributes(IMethodSymbol method)
    {
        var isOneWay = false;
        foreach (var attribute in method.GetAttributes())
        {
            if (IsOnewayAttribute(attribute))
                isOneWay = true;
            else if (IsTimeoutAttribute(attribute) &&
                TryGetTimeoutSeconds(attribute, out var seconds) &&
                !TryValidateTimeoutSeconds(seconds, out _))
            {
                return true;
            }
        }
        return isOneWay && !IsValidOnewayReturnType(method.ReturnType);
    }

    private static bool ContainsPointerOrFunctionPointer(ITypeSymbol type)
        => type switch
        {
            IPointerTypeSymbol => true,
            IFunctionPointerTypeSymbol => true,
            IArrayTypeSymbol arrayType => ContainsPointerOrFunctionPointer(arrayType.ElementType),
            INamedTypeSymbol namedType => namedType.TypeArguments.Any(ContainsPointerOrFunctionPointer),
            _ => false
        };
}
