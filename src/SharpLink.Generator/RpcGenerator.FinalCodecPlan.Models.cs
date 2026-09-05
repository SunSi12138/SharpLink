namespace SharpLink.Generator;

internal enum FinalCodecPlanKind
{
    Primitive,
    Enum,
    GeneratedDto,
    Collection,
    UnsafeBlit,
    Custom,
    Adapter,
    Referenced
}

internal enum FinalCollectionWireStrategy
{
    ChildCodec,
    RawBlit,
    DateTimeOffsetCanonical
}

internal enum FinalEffectiveLayoutKind
{
    Sequential,
    Explicit,
    Auto
}

internal sealed record FinalUnsafeBlitAbiPlan(
    string Endianness,
    int NativePointerWidth,
    string Version);

internal abstract record FinalCodecPlan(string TypeName, FinalCodecPlanKind Kind);

internal sealed record FinalPrimitiveCodecPlan(
    string TypeName,
    string Family,
    ImmutableArray<string> SemanticParts,
    string? ChildType = null)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.Primitive);

internal sealed record FinalEnumCodecPlan(
    string TypeName,
    string UnderlyingType,
    string DeclarationSemantic)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.Enum);

internal enum FinalDtoMemberWireStrategy
{
    String,
    Fixed,
    ChildCodec
}

internal sealed record FinalDtoMemberPlan(
    uint FieldId,
    GeneratedMemberKind Kind,
    bool Required,
    bool Nullable,
    bool NonNullableReference,
    FinalDtoMemberWireStrategy WireStrategy,
    string? WireSemantic,
    string? ChildType);

internal sealed record FinalGeneratedDtoCodecPlan(
    string TypeName,
    bool IsReferenceType,
    ImmutableArray<FinalDtoMemberPlan> Members)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.GeneratedDto);

internal sealed record FinalCollectionCodecPlan(
    string TypeName,
    GeneratedCodecKind CollectionKind,
    FinalCollectionWireStrategy WireStrategy,
    string? ElementType,
    string? KeyType,
    string? ValueType,
    FinalPhysicalLayoutPlan? RawElementLayout,
    string? StrategySemantic)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.Collection);

internal sealed record FinalUnsafeBlitCodecPlan(
    string TypeName,
    FinalUnsafeBlitAbiPlan Abi,
    FinalPhysicalLayoutPlan Layout,
    ImmutableArray<FinalCodecAutoLayoutHazardDescriptor> AutoLayoutHazards)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.UnsafeBlit);

internal sealed record FinalCustomCodecPlan(
    string TypeName,
    RpcHashValue OpaqueSemanticIdentity,
    RpcHashValue ClosedTargetLogicalIdentity,
    string CodecTypeName)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.Custom);

internal sealed record FinalAdapterCodecPlan(
    string TypeName,
    RpcHashValue OpaqueSemanticIdentity,
    RpcHashValue ClosedTargetLogicalIdentity,
    string AdapterTypeName,
    string AdapterId)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.Adapter);

internal sealed record FinalReferencedCodecPlan(
    string TypeName,
    RpcHashValue CodecHash)
    : FinalCodecPlan(TypeName, FinalCodecPlanKind.Referenced);

internal abstract record FinalPhysicalLayoutPlan;

internal sealed record FinalPrimitivePhysicalPlan(
    string Token,
    string? FrameworkRawAbi = null)
    : FinalPhysicalLayoutPlan;

internal sealed record FinalEnumPhysicalPlan(
    FinalPhysicalLayoutPlan Underlying,
    string DeclarationSemantic)
    : FinalPhysicalLayoutPlan;

internal sealed record FinalPointerPhysicalPlan(string TargetLogicalIdentity)
    : FinalPhysicalLayoutPlan;

internal sealed record FinalFunctionPointerPhysicalPlan(string SignatureSemantic)
    : FinalPhysicalLayoutPlan;

internal sealed record FinalFixedBufferPhysicalPlan(
    int Length,
    FinalPhysicalLayoutPlan Element)
    : FinalPhysicalLayoutPlan;

internal sealed record FinalPhysicalFieldPlan(
    int? Offset,
    FinalPhysicalLayoutPlan Layout);

internal sealed record FinalStructPhysicalPlan(
    FinalEffectiveLayoutKind LayoutKind,
    int Pack,
    int Size,
    int? InlineArrayLength,
    ImmutableArray<FinalPhysicalFieldPlan> Fields)
    : FinalPhysicalLayoutPlan;

internal readonly record struct FinalCodecAutoLayoutHazardDescriptor(
    string TypeName,
    string FieldPath,
    Location Location);

internal readonly record struct FinalCodecAutoLayoutDiagnosticModel(
    string PayloadType,
    string TypeName,
    string FieldPath,
    Location Location);

internal sealed class FinalCodecGraph(
    IReadOnlyDictionary<string, FinalCodecPlan> plans,
    ImmutableArray<string> rootTypes)
{
    internal IReadOnlyDictionary<string, FinalCodecPlan> Plans { get; } = plans;
    internal ImmutableArray<string> RootTypes { get; } = rootTypes;
}
