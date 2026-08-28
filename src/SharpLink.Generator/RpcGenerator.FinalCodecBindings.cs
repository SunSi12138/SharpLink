namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static RpcInterfaceModel? BindFinalCodecSelections(
        RpcInterfaceModel? model,
        DtoGenerationResult codecs)
    {
        if (model is null)
            return null;

        // CLR shape only says whether the Native implementation *could* inline a value. The
        // finalized Contract Codec graph decides whether that optimization is legal on RPC wire.
        var selectedTypes = new HashSet<string>(
            codecs.ContractManifestCodecs
                .Where(static codec => codec.Kind is not (GeneratedCodecKind.Native or GeneratedCodecKind.UnsafeBlit))
                .Select(static codec => codec.TypeName),
            StringComparer.Ordinal);
        var methods = model.Methods
            .Select(method =>
            {
                var responsePayloadType = method.IsStreamReturn
                    ? method.StreamItemType
                    : method.GenericArgumentType;
                var responseUsesSelectedCodec = responsePayloadType is not null &&
                                                selectedTypes.Contains(responsePayloadType);
                return method with
                {
                    Parameters = method.Parameters
                        .Select(parameter =>
                        {
                            var parameterUsesSelectedCodec = selectedTypes.Contains(parameter.Type);
                            var streamItemUsesSelectedCodec = parameter.StreamItemType is not null &&
                                                              selectedTypes.Contains(parameter.StreamItemType);
                            return parameter with
                            {
                                IsBlittable = parameter.IsBlittable && !parameterUsesSelectedCodec,
                                EnumUnderlyingType = parameterUsesSelectedCodec
                                    ? null
                                    : parameter.EnumUnderlyingType,
                                StreamItemEnumUnderlyingType = streamItemUsesSelectedCodec
                                    ? null
                                    : parameter.StreamItemEnumUnderlyingType
                            };
                        })
                        .ToImmutableArray(),
                    ResponseEnumUnderlyingType = responseUsesSelectedCodec
                        ? null
                        : method.ResponseEnumUnderlyingType,
                    StreamItemEnumUnderlyingType = method.IsStreamReturn && responseUsesSelectedCodec
                        ? null
                        : method.StreamItemEnumUnderlyingType
                };
            })
            .ToImmutableArray();

        return model with { Methods = methods };
    }
}
