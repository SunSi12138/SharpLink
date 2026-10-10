namespace SharpLink.Generator;

public partial class RpcGenerator
{
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
