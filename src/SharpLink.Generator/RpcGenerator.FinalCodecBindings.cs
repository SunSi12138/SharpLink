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
            codecs.ContractManifestCodecs.Select(static codec => codec.TypeName),
            StringComparer.Ordinal);
        var methods = model.Methods
            .Select(method => method with
            {
                Parameters = method.Parameters
                    .Select(parameter => parameter with
                    {
                        // Unary request framing is a property of the final Contract Codec
                        // selection for this exact payload type. Recompute the native inline
                        // candidate here instead of carrying the assembly-wide route heuristic
                        // from syntax analysis into proxy/stub/manifest emission.
                        IsBlittable = IsInlineFixedRpcParameter(parameter) &&
                                      !selectedTypes.Contains(parameter.Type)
                    })
                    .ToImmutableArray()
            })
            .ToImmutableArray();

        return model with { Methods = methods };
    }

    private static bool IsInlineFixedRpcParameter(RpcParameterModel parameter)
    {
        if (parameter.IsStream || parameter.IsCancellationToken || parameter.IsCallOptions)
            return false;
        if (!string.IsNullOrEmpty(parameter.EnumUnderlyingType))
            return true;

        var type = parameter.Type.StartsWith("global::", StringComparison.Ordinal)
            ? parameter.Type.Substring("global::".Length)
            : parameter.Type;
        return type is
            "bool" or "byte" or "sbyte" or "short" or "ushort" or "char" or
            "int" or "uint" or "float" or "long" or "ulong" or "double" or
            "System.Boolean" or "System.Byte" or "System.SByte" or
            "System.Int16" or "System.UInt16" or "System.Char" or
            "System.Int32" or "System.UInt32" or "System.Single" or
            "System.Int64" or "System.UInt64" or "System.Double" or
            "System.Half" or "System.Guid" or "System.TimeSpan" or
            "System.Int128" or "System.UInt128";
    }
}
