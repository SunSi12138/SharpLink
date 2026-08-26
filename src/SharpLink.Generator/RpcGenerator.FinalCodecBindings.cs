namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static RpcInterfaceModel? BindFinalCodecSelections(
        RpcInterfaceModel? model,
        DtoGenerationResult codecs)
    {
        if (model is null)
            return null;

        var selectedTypes = new HashSet<string>(
            codecs.ContractManifestCodecs
                .Where(static codec => codec.Kind is not (GeneratedCodecKind.Native or GeneratedCodecKind.UnsafeBlit))
                .Select(static codec => codec.TypeName),
            StringComparer.Ordinal);
        var methods = model.Methods
            .Select(method => method with
            {
                Parameters = method.Parameters
                    .Select(parameter => parameter with
                    {
                        // Syntax analysis records only whether the CLR payload is eligible
                        // for the inline fixed path. The final Contract assembly Codec graph decides
                        // whether that candidate remains inline for emitted RPC artifacts.
                        IsBlittable = parameter.IsBlittable &&
                                      !selectedTypes.Contains(parameter.Type)
                    })
                    .ToImmutableArray()
            })
            .ToImmutableArray();

        return model with { Methods = methods };
    }
}
