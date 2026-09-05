namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static RpcInterfaceModel? GetInterfaceModelOrNull(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return null;
        if (!InheritsIService(symbol))
            return null;

        return HasInvalidRpcMethod(symbol) ? null : CreateInterfaceModel(symbol);
    }

    private static RpcContractDiagnosticModel? GetRpcContractDiagnosticOrNull(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return null;
        if (!InheritsIService(symbol))
        {
            return new RpcContractDiagnosticModel(
                RpcContractDiagnosticKind.Inheritance,
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                symbol.Locations.FirstOrDefault());
        }
        if (!IsPubliclyReachableContract(symbol))
        {
            return new RpcContractDiagnosticModel(
                RpcContractDiagnosticKind.Accessibility,
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                symbol.Locations.FirstOrDefault());
        }
        return null;
    }

    private static ImmutableArray<InvalidRpcMethodModel> GetInvalidRpcMethods(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        var list = ImmutableArray.CreateBuilder<InvalidRpcMethodModel>();
        list.AddRange(GetInvalidRpcMethodDiagnostics(context, cancellationToken));
        if (context.TargetSymbol is INamedTypeSymbol symbol &&
            symbol.TypeKind == TypeKind.Interface &&
            InheritsIService(symbol))
        {
            AddUnsupportedContractMemberDiagnostics(symbol, list);
        }
        return list.ToImmutable();
    }

    private static bool HasInvalidRpcMethod(INamedTypeSymbol interfaceSymbol)
    {
        if (interfaceSymbol.Arity > 0 || HasGenericContainingType(interfaceSymbol) ||
            !IsPubliclyReachableContract(interfaceSymbol) ||
            HasUnsupportedContractMember(interfaceSymbol) ||
            HasInvalidRpcMethodShape(interfaceSymbol))
        {
            return true;
        }

        return GetContractMethods(interfaceSymbol)
            .Any(m =>
                m.IsGenericMethod ||
                HasTypeParameter(m.ReturnType) ||
                m.Parameters.Any(p => HasTypeParameter(p.Type)) ||
                m.Parameters.Count(p => IsAsyncEnumerable(p.Type, out _)) > sbyte.MaxValue);
    }

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

    private static bool IsPubliclyReachableContract(INamedTypeSymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
                return false;
        }
        return true;
    }
}
