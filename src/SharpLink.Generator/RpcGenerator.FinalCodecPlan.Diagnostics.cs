namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        internal static ImmutableArray<FinalCodecAutoLayoutDiagnosticModel> BuildUnsafeBlitAutoLayoutDiagnostics(
            FinalCodecGraph graph)
        {
            var diagnostics = ImmutableArray.CreateBuilder<FinalCodecAutoLayoutDiagnosticModel>();
            var dedup = new HashSet<(string Payload, string Type, string Path)>();

            foreach (var payload in graph.RootTypes)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                Visit(payload);

                void Visit(string typeName)
                {
                    if (!visited.Add(typeName) || !graph.Plans.TryGetValue(typeName, out var plan))
                        return;
                    if (plan is FinalUnsafeBlitCodecPlan unsafeBlit)
                    {
                        foreach (var hazard in unsafeBlit.AutoLayoutHazards)
                        {
                            if (dedup.Add((payload, hazard.TypeName, hazard.FieldPath)))
                            {
                                diagnostics.Add(new FinalCodecAutoLayoutDiagnosticModel(
                                    payload,
                                    hazard.TypeName,
                                    hazard.FieldPath,
                                    hazard.Location));
                            }
                        }
                        return;
                    }

                    foreach (var dependency in GetFinalCodecPlanDependencies(plan))
                        Visit(dependency);
                }
            }

            return diagnostics
                .OrderBy(static item => item.PayloadType, StringComparer.Ordinal)
                .ThenBy(static item => item.TypeName, StringComparer.Ordinal)
                .ThenBy(static item => item.FieldPath, StringComparer.Ordinal)
                .ToImmutableArray();
        }
    }
}
