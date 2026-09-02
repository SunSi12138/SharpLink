namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private static readonly FinalUnsafeBlitAbiPlan UnsafeBlitAbi =
            new("little-endian", 8, "v3");

        private readonly Dictionary<string, RpcHashValue?> _opaqueSemanticIdentityCache =
            new(StringComparer.Ordinal);

        internal FinalCodecGraph ResolveFinalCodecGraph(
            bool includeSerializable,
            bool includeContracts)
        {
            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(
                _compilation.Assembly.GlobalNamespace,
                roots,
                includeSerializable,
                includeContracts);

            var plans = new Dictionary<string, FinalCodecPlan>(StringComparer.Ordinal);
            var resolving = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in roots.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                if (!_failed.Contains(pair.Key))
                    ResolveFinalCodecPlan(pair.Value, plans, resolving);
            }

            // Enum declaration semantics can be required by generated metadata even when the
            // enclosing runtime Codec is a raw physical plan such as UnsafeBlit<Nullable<TEnum>>.
            // Materialize those reached enum nodes here so every downstream consumer observes the
            // same complete final graph without consulting Roslyn again.
            foreach (var enumModel in _enums.Values.OrderBy(static item => item.TypeName, StringComparer.Ordinal))
            {
                if (_failed.Contains(enumModel.TypeName) || plans.ContainsKey(enumModel.TypeName))
                    continue;
                if (!TryResolveReachableType(enumModel.TypeName, out var type) ||
                    type is not INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
                {
                    throw new InvalidOperationException(
                        $"Final RPC Codec graph cannot resolve reached enum metadata for '{enumModel.TypeName}'.");
                }
                ResolveFinalCodecPlan(enumType, plans, resolving);
            }

            return new FinalCodecGraph(
                plans,
                roots.Keys.Where(type => !_failed.Contains(type))
                    .OrderBy(static type => type, StringComparer.Ordinal)
                    .ToImmutableArray());
        }
    }
}
