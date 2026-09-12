namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static ImmutableArray<GeneratedCodecModel> GetContractManifestCodecs(DtoGenerationResult result)
    {
        var codecsByType = result.Codecs
            .ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        foreach (var codec in result.ContractCodecs)
            codecsByType[codec.TypeName] = codec;

        return codecsByType.Values
            .OrderBy(static codec => codec.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}
