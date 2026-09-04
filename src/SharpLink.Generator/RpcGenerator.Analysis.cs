namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static bool IsAsyncEnumerable(ITypeSymbol type, out ITypeSymbol? itemType)
    {
        itemType = null;
        if (type is not INamedTypeSymbol named || named.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.IAsyncEnumerable<T>")
            return false;
        itemType = named.TypeArguments[0];
        return true;
    }

    private static ImmutableArray<InvalidRpcMethodModel> GetInvalidRpcMethods(GeneratorAttributeSyntaxContext context, CancellationToken _)
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

        AddUnsupportedContractMemberDiagnostics(symbol, list);

        return list.ToImmutable();
    }

    private static ImmutableArray<InvalidCancellationTokenMethodModel> GetInvalidCancellationTokenMethods(GeneratorAttributeSyntaxContext context, CancellationToken _)
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

    private static ImmutableArray<InvalidStreamCountMethodModel> GetInvalidStreamCountMethods(GeneratorAttributeSyntaxContext context, CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return ImmutableArray<InvalidStreamCountMethodModel>.Empty;
        if (!InheritsIService(symbol))
            return ImmutableArray<InvalidStreamCountMethodModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidStreamCountMethodModel>();
        foreach (var method in GetContractMethods(symbol))
        {
            var streamCount = method.Parameters.Count(p => IsAsyncEnumerable(p.Type, out var _));
            if (streamCount <= sbyte.MaxValue)
                continue;

            list.Add(new InvalidStreamCountMethodModel(
                method.Name,
                streamCount,
                method.Locations.FirstOrDefault()));
        }

        return list.ToImmutable();
    }

    private static ImmutableArray<NonCancellableRpcMethodModel> GetNonCancellableRpcMethods(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface ||
            !InheritsIService(symbol))
        {
            return ImmutableArray<NonCancellableRpcMethodModel>.Empty;
        }

        var list = ImmutableArray.CreateBuilder<NonCancellableRpcMethodModel>();
        foreach (var method in GetContractMethods(symbol))
        {
            if (method.Parameters.Any(IsCancellationTokenParameter) ||
                method.GetAttributes().Any(IsNonCancellableAttribute) ||
                IsStreamingMethod(method))
            {
                continue;
            }

            list.Add(new NonCancellableRpcMethodModel(method.Name, method.Locations.FirstOrDefault()));
        }
        return list.ToImmutable();
    }

    private static ImmutableArray<StreamingWithoutCancellationModel> GetStreamingWithoutCancellationMethods(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface ||
            !InheritsIService(symbol))
        {
            return ImmutableArray<StreamingWithoutCancellationModel>.Empty;
        }

        var list = ImmutableArray.CreateBuilder<StreamingWithoutCancellationModel>();
        foreach (var method in GetContractMethods(symbol))
        {
            if (!IsStreamingMethod(method) ||
                method.Parameters.Any(IsCancellationTokenParameter) ||
                method.GetAttributes().Any(IsNonCancellableAttribute))
            {
                continue;
            }

            list.Add(new StreamingWithoutCancellationModel(method.Name, method.Locations.FirstOrDefault()));
        }
        return list.ToImmutable();
    }

    private static ImmutableArray<ConflictingCancellationContractModel> GetConflictingCancellationContractMethods(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface ||
            !InheritsIService(symbol))
        {
            return ImmutableArray<ConflictingCancellationContractModel>.Empty;
        }

        var list = ImmutableArray.CreateBuilder<ConflictingCancellationContractModel>();
        foreach (var method in GetContractMethods(symbol))
        {
            if (!method.Parameters.Any(IsCancellationTokenParameter) ||
                !method.GetAttributes().Any(IsNonCancellableAttribute))
            {
                continue;
            }

            list.Add(new ConflictingCancellationContractModel(method.Name, method.Locations.FirstOrDefault()));
        }
        return list.ToImmutable();
    }

    private static bool IsStreamingMethod(IMethodSymbol method)
        => IsAsyncEnumerable(method.ReturnType, out _) ||
           method.Parameters.Any(static parameter => IsAsyncEnumerable(parameter.Type, out _));

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

    private static ImmutableArray<InvalidGenericUsageModel> GetInvalidGenericUsage(GeneratorAttributeSyntaxContext context, CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return ImmutableArray<InvalidGenericUsageModel>.Empty;
        if (!InheritsIService(symbol))
            return ImmutableArray<InvalidGenericUsageModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidGenericUsageModel>();
        if (symbol.Arity > 0 || HasGenericContainingType(symbol))
        {
            list.Add(new InvalidGenericUsageModel(
                symbol.Name,
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                symbol.Locations.FirstOrDefault()));
        }

        foreach (var method in GetContractMethods(symbol))
        {
            var hasGenericUsage = method.IsGenericMethod ||
                                  HasTypeParameter(method.ReturnType) ||
                                  method.Parameters.Any(p => HasTypeParameter(p.Type));
            if (!hasGenericUsage)
                continue;

            list.Add(new InvalidGenericUsageModel(
                method.Name,
                method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                method.Locations.FirstOrDefault()));
        }

        return list.ToImmutable();
    }

    private static bool HasInvalidRpcMethod(INamedTypeSymbol interfaceSymbol)
    {
        if (interfaceSymbol.Arity > 0 || HasGenericContainingType(interfaceSymbol) ||
            !IsPubliclyReachableContract(interfaceSymbol) ||
            HasUnsupportedContractMember(interfaceSymbol) ||
            GetConflictingInheritedRpcSignatures(interfaceSymbol).Any())
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
                m.IsGenericMethod ||
                HasTypeParameter(m.ReturnType) ||
                m.Parameters.Any(p => HasTypeParameter(p.Type)) ||
                m.Parameters.Count(IsCancellationTokenParameter) > 1 ||
                !HasValidControlParameterOrder(m) ||
                m.Parameters.Count(p => IsAsyncEnumerable(p.Type, out _)) > sbyte.MaxValue ||
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

    private static void AddUnsupportedContractMemberDiagnostics(
        INamedTypeSymbol symbol,
        ImmutableArray<InvalidRpcMethodModel>.Builder diagnostics)
    {
        HashSet<ISymbol>? seen = null;
        AddFrom(symbol);
        foreach (var contract in symbol.AllInterfaces)
        {
            if (!IsIService(contract))
                AddFrom(contract);
        }
        return;

        void AddFrom(INamedTypeSymbol contract)
        {
            foreach (var member in contract.GetMembers())
            {
                if (!IsUnsupportedAbstractContractMember(member) ||
                    !(seen ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default)).Add(member.OriginalDefinition))
                {
                    continue;
                }
                diagnostics.Add(new InvalidRpcMethodModel(
                    InvalidRpcMethodKind.ContractMember,
                    GetContractMemberName(member),
                    member switch
                    {
                        IEventSymbol => "abstract events cannot be implemented by an RPC proxy",
                        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } =>
                            "static abstract operators and conversions cannot be implemented by an RPC proxy",
                        IMethodSymbol => "non-public abstract methods cannot be exposed as RPC routes",
                        _ => "abstract properties and indexers cannot be represented as RPC routes"
                    },
                    member.Locations.FirstOrDefault()));
            }
        }
    }

    private static bool HasUnsupportedContractMember(INamedTypeSymbol symbol)
    {
        if (HasUnsupportedContractMemberDirect(symbol))
            return true;
        foreach (var contract in symbol.AllInterfaces)
        {
            if (!IsIService(contract) && HasUnsupportedContractMemberDirect(contract))
                return true;
        }
        return false;
    }

    private static bool HasUnsupportedContractMemberDirect(INamedTypeSymbol contract)
    {
        foreach (var member in contract.GetMembers())
        {
            if (IsUnsupportedAbstractContractMember(member))
                return true;
        }
        return false;
    }

    private static string GetContractMemberName(ISymbol member)
        => member is IPropertySymbol { IsIndexer: true } ? "this[]" : member.Name;

    private static bool IsUnsupportedAbstractContractMember(ISymbol member)
        => member is IPropertySymbol { IsAbstract: true } or IEventSymbol { IsAbstract: true } ||
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
           };

    private static bool HasGenericContainingType(INamedTypeSymbol symbol)
    {
        for (var current = symbol.ContainingType; current is not null; current = current.ContainingType)
        {
            if (current.Arity != 0)
                return true;
        }
        return false;
    }

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

    private static bool HasTypeParameter(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.TypeParameter)
            return true;

        return type switch
        {
            IArrayTypeSymbol arrayType => HasTypeParameter(arrayType.ElementType),
            IPointerTypeSymbol pointerType => HasTypeParameter(pointerType.PointedAtType),
            INamedTypeSymbol namedType => namedType.IsUnboundGenericType ||
                                         namedType.TypeArguments.Any(HasTypeParameter),
            _ => false
        };
    }

    private static bool ContainsRefLikeType(ITypeSymbol type)
        => type switch
        {
            INamedTypeSymbol { IsRefLikeType: true } => true,
            IArrayTypeSymbol arrayType => ContainsRefLikeType(arrayType.ElementType),
            IPointerTypeSymbol pointerType => ContainsRefLikeType(pointerType.PointedAtType),
            INamedTypeSymbol namedType => namedType.TypeArguments.Any(ContainsRefLikeType),
            _ => false
        };

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
