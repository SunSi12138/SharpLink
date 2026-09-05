namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private FinalUnionCodecPlan? ResolveGeneratedUnionPlan(
            ITypeSymbol type,
            GeneratedCodecModel model,
            Dictionary<string, FinalCodecPlan> plans,
            HashSet<string> resolving)
        {
            if (!TryGetNativeUnionCases(type, reportDiagnostics: false, out var cases) || cases.IsDefaultOrEmpty)
            {
                throw new InvalidOperationException(
                    $"Final native union selection for '{model.TypeName}' has no validated union cases.");
            }

            var finalCases = ImmutableArray.CreateBuilder<FinalUnionCasePlan>(cases.Length);
            foreach (var unionCase in cases.OrderBy(static item => item.Discriminator))
            {
                var child = ResolveFinalCodecPlan(unionCase.Type, plans, resolving);
                if (child is null)
                    return null;

                finalCases.Add(new FinalUnionCasePlan(
                    unionCase.Discriminator,
                    child.TypeName,
                    GetUnionCaseLogicalIdentity(unionCase.Type)));
            }

            return new FinalUnionCodecPlan(
                model.TypeName,
                NativeUnionWireSemantic,
                finalCases.ToImmutable());
        }

        private static RpcHashValue GetUnionCaseLogicalIdentity(ITypeSymbol caseType)
        {
            var parts = new List<string> { "union-case-target/v1" };
            AppendClosedTargetLogicalIdentity(caseType, parts);
            return Hashing.GetSemanticHash(parts.ToArray());
        }
    }
}