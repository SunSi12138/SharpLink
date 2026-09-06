namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private static RpcHashValue HashUnionPlan(
            FinalUnionCodecPlan plan,
            FinalCodecGraph graph,
            Dictionary<string, RpcHashValue> cache,
            HashSet<string> stack)
        {
            var parts = new List<string>
            {
                "codec/v1",
                "union",
                plan.WireSemantic,
                plan.Cases.Length.ToString(InvariantCulture)
            };
            foreach (var item in plan.Cases
                         .OrderBy(static item => item.Discriminator)
                         .ThenBy(static item => item.CaseType, StringComparer.Ordinal))
            {
                parts.Add(item.Discriminator.ToString(InvariantCulture));
                parts.Add(item.LogicalIdentity.ToHex());
                parts.Add(HashRequiredChild(item.CaseType, graph, cache, stack).ToHex());
            }
            return Hashing.GetSemanticHash(parts.ToArray());
        }
    }
}
