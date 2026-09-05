namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private FinalUnionCodecPlan CreateFinalUnionPlan(
            ITypeSymbol type,
            GeneratedCodecModel model)
        {
            if (!TryGetNativeUnionCases(type, reportDiagnostics: false, out var cases) || cases.IsDefaultOrEmpty)
            {
                throw new InvalidOperationException(
                    $"Final native union selection for '{model.TypeName}' has no validated union cases.");
            }

            var finalCases = cases
                .OrderBy(static item => item.Discriminator)
                .Select(static unionCase => new FinalUnionCasePlan(
                    unionCase.Discriminator,
                    GetTypeName(unionCase.Type),
                    GetUnionCaseLogicalIdentity(unionCase.Type)))
                .ToImmutableArray();

            return new FinalUnionCodecPlan(
                model.TypeName,
                NativeUnionWireSemantic,
                finalCases);
        }

        private static RpcHashValue GetUnionCaseLogicalIdentity(ITypeSymbol caseType)
        {
            var parts = new List<string> { "union-case-target/v1" };
            AppendClosedTargetLogicalIdentity(caseType, parts);
            return Hashing.GetSemanticHash(parts.ToArray());
        }
    }
}