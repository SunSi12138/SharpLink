namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static RpcInterfaceModel? BindFinalCodecSelections(
        RpcInterfaceModel? model,
        DtoGenerationResult codecs)
    {
        if (model is null)
            return null;

        var contractPolicy = codecs.ContractPolicies.FirstOrDefault(policy =>
            string.Equals(policy.ContractTypeName, model.FullName, StringComparison.Ordinal));
        var selectedTypes = new HashSet<string>(
            contractPolicy?.Codecs.Select(static codec => codec.TypeName) ??
            codecs.ContractManifestCodecs.Select(static codec => codec.TypeName),
            StringComparer.Ordinal);
        var methods = model.Methods
            .Select(method => method with
            {
                Parameters = method.Parameters
                    .Select(parameter => parameter with
                    {
                        // Syntax analysis records only whether the CLR payload is eligible for the
                        // inline fixed path. The final Codec graph for *this Contract* decides whether
                        // that candidate remains inline; policy from a sibling Contract must not alter
                        // request framing.
                        IsBlittable = parameter.IsBlittable &&
                                      !selectedTypes.Contains(parameter.Type)
                    })
                    .ToImmutableArray()
            })
            .ToImmutableArray();

        return model with { Methods = methods };
    }
}
