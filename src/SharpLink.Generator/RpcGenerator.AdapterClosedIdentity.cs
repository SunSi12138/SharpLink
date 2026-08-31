namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private RpcHashValue GetAdapterTargetLogicalIdentity(GeneratedCodecModel model)
        {
            if (!TryResolveReachableType(model.TypeName, out var targetType))
            {
                throw new InvalidOperationException(
                    $"Final RPC Codec graph cannot resolve adapter target '{model.TypeName}' while hashing its closed Codec semantics.");
            }

            return Hashing.GetSemanticHash(
                "adapter-target/v1",
                targetType.ContainingAssembly?.Identity.Name ?? string.Empty,
                GetTypeName(targetType));
        }
    }
}
