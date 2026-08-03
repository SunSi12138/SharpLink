namespace SharpLink.Generator;

public partial class RpcGenerator
{
    internal static bool CanGenerateContractPayloadCodecs(
        Compilation compilation,
        INamedTypeSymbol contract,
        CancellationToken cancellationToken)
        => new DtoAnalysisState(compilation, cancellationToken)
            .CanGenerateContractPayloadCodecs(contract);

    internal static bool CanGenerateDtoAfterSealing(
        Compilation compilation,
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
        => new DtoAnalysisState(compilation, cancellationToken)
            .CanGenerateDtoAfterSealing(type);

    internal static bool CanGenerateDtoAfterPublicization(
        Compilation compilation,
        INamedTypeSymbol type,
        ImmutableArray<INamedTypeSymbol> publicizedTypes,
        CancellationToken cancellationToken)
        => new DtoAnalysisState(compilation, cancellationToken)
            .CanGenerateDtoAfterPublicization(type, publicizedTypes);
}
