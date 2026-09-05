namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private FinalCodecGraph ApplySharpPackSidecarCodecIdentities(FinalCodecGraph graph)
        {
            Dictionary<string, FinalCodecPlan>? updated = null;
            foreach (var pair in graph.Plans)
            {
                if (pair.Value is not FinalAdapterCodecPlan adapter ||
                    !string.Equals(adapter.AdapterId, SharpPackAdapterId, StringComparison.Ordinal) ||
                    !string.Equals(adapter.AdapterTypeName, SharpPackAdapterTypeName, StringComparison.Ordinal) ||
                    !TryResolveReachableType(pair.Key, out var rootType))
                {
                    continue;
                }

                var analysis = new SharpPackSidecarAnalysis(_compilation);
                analysis.AnalyzeRoot(rootType);
                var result = analysis.ToResult();
                if (!result.Diagnostics.IsDefaultOrEmpty || result.Sidecars.IsDefaultOrEmpty)
                    continue;

                updated ??= graph.Plans.ToDictionary(
                    static item => item.Key,
                    static item => item.Value,
                    StringComparer.Ordinal);
                updated[pair.Key] = adapter with
                {
                    ClosedTargetLogicalIdentity = Hashing.GetSemanticHash(
                        "adapter-target/sharppack-sidecar/v1",
                        adapter.ClosedTargetLogicalIdentity.ToHex(),
                        GetSharpPackSidecarWireIdentity(result.Sidecars).ToHex())
                };
            }

            return updated is null
                ? graph
                : new FinalCodecGraph(updated, graph.RootTypes);
        }
    }

    private static RpcHashValue GetSharpPackSidecarWireIdentity(
        ImmutableArray<SharpPackSidecarModel> sidecars)
    {
        var parts = new List<string> { "sharppack-sidecar-wire/v1" };
        foreach (var sidecar in sidecars.OrderBy(static item => item.TypeName, StringComparer.Ordinal))
        {
            parts.Add(sidecar.TypeName);
            parts.Add(sidecar.IsReferenceType ? "ref" : "value");
            parts.Add(sidecar.Members.Length.ToString(InvariantCulture));
            foreach (var member in sidecar.Members)
            {
                parts.Add(member.Order.ToString(InvariantCulture));
                parts.Add(member.Name);
                parts.Add(member.TypeName);
            }
        }
        return Hashing.GetSemanticHash(parts.ToArray());
    }
}
