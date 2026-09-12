namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        internal bool TryResolveSharpPackReachableType(string typeName, out ITypeSymbol type)
            => TryResolveReachableType(typeName, out type);
    }
}
