namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    private static ContractGeneratorResult RunContractGenerator(string source)
        => RunContractGenerator(
            UseCurrentIdentitySdk(source),
            baseline: null,
            outputPath: null);

    private static ContractGeneratorResult RunContractGenerator(string source, string? baseline)
        => RunContractGenerator(
            UseCurrentIdentitySdk(source),
            baseline,
            outputPath: null);
}
