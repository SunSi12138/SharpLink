namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static ImmutableArray<GeneratedUnsafeBlitRequirementModel> BuildUnsafeBlitRequirements(
        params FinalCodecGraph[] graphs)
        => graphs
            .SelectMany(static graph => graph.Plans.Values)
            .OfType<FinalUnsafeBlitCodecPlan>()
            .GroupBy(static plan => plan.TypeName, StringComparer.Ordinal)
            .Select(static group => group.First())
            .Select(static plan => new GeneratedUnsafeBlitRequirementModel(
                plan.TypeName,
                plan.Abi.NativePointerWidth,
                RequiresDateTimeOffsetRawAbi(plan.Layout)))
            .OrderBy(static requirement => requirement.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

    private static bool RequiresDateTimeOffsetRawAbi(FinalPhysicalLayoutPlan plan)
        => plan switch
        {
            FinalPrimitivePhysicalPlan primitive =>
                primitive.FrameworkRawAbi?.StartsWith(
                    "framework-raw/datetimeoffset/",
                    StringComparison.Ordinal) == true,
            FinalEnumPhysicalPlan enumPlan => RequiresDateTimeOffsetRawAbi(enumPlan.Underlying),
            FinalFixedBufferPhysicalPlan buffer => RequiresDateTimeOffsetRawAbi(buffer.Element),
            FinalStructPhysicalPlan structure =>
                structure.Fields.Any(static field => RequiresDateTimeOffsetRawAbi(field.Layout)),
            _ => false
        };
}
