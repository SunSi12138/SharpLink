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

            var parts = new List<string> { "adapter-target/v2" };
            AppendClosedTargetLogicalIdentity(targetType, parts);
            return Hashing.GetSemanticHash(parts.ToArray());
        }

        private static void AppendClosedTargetLogicalIdentity(ITypeSymbol type, List<string> parts)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    parts.Add("array");
                    parts.Add(array.Rank.ToString(InvariantCulture));
                    AppendClosedTargetLogicalIdentity(array.ElementType, parts);
                    return;
                case IPointerTypeSymbol pointer:
                    parts.Add("pointer");
                    AppendClosedTargetLogicalIdentity(pointer.PointedAtType, parts);
                    return;
                case IFunctionPointerTypeSymbol functionPointer:
                    parts.Add("function-pointer");
                    parts.Add(functionPointer.Signature.RefKind.ToString());
                    AppendClosedTargetLogicalIdentity(functionPointer.Signature.ReturnType, parts);
                    parts.Add(functionPointer.Signature.Parameters.Length.ToString(InvariantCulture));
                    foreach (var parameter in functionPointer.Signature.Parameters)
                    {
                        parts.Add(parameter.RefKind.ToString());
                        AppendClosedTargetLogicalIdentity(parameter.Type, parts);
                    }
                    return;
                case INamedTypeSymbol named:
                    parts.Add("named");
                    parts.Add(named.ContainingAssembly?.Identity.Name ?? string.Empty);
                    if (named.ContainingType is not null)
                    {
                        AppendClosedTargetLogicalIdentity(named.ContainingType, parts);
                    }
                    else
                    {
                        parts.Add(named.ContainingNamespace?.ToDisplayString() ?? string.Empty);
                    }
                    parts.Add(named.MetadataName);
                    parts.Add(named.TypeArguments.Length.ToString(InvariantCulture));
                    foreach (var argument in named.TypeArguments)
                        AppendClosedTargetLogicalIdentity(argument, parts);
                    return;
                case ITypeParameterSymbol parameter:
                    throw new InvalidOperationException(
                        $"Adapter target logical identity requires a closed type, but '{parameter.Name}' is still open.");
                default:
                    parts.Add(type.TypeKind.ToString());
                    parts.Add(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                    return;
            }
        }
    }
}
