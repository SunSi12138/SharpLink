namespace SharpLink.Generator;

public partial class RpcGenerator
{
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
}
