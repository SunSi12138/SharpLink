namespace SharpLink.Generator;

internal sealed record DtoCodecAnalysisResult(
    EquatableArray<DtoCodecAnalysisModel> Codecs,
    EquatableArray<DtoCodecAnalysisModel> ContractCodecs);

internal sealed record DtoCodecAnalysisModel(
    string TypeName,
    string CodecName,
    bool IsReferenceType,
    ulong CodecHashHigh,
    ulong CodecHashLow,
    EquatableArray<DtoMemberAnalysisModel> Members,
    EquatableArray<string> ConstructorMembers);

internal sealed record DtoMemberAnalysisModel(
    string Name,
    string Identifier,
    string TypeName,
    uint FieldId,
    GeneratedMemberKind Kind,
    string? FixedTypeName,
    int FixedSize,
    bool Required,
    bool NonNullableReference,
    bool InitializerBound);

public partial class RpcGenerator
{
    internal static DtoCodecAnalysisResult CreateDtoCodecAnalysisResult(
        ImmutableArray<GeneratedCodecModel> codecs,
        ImmutableArray<GeneratedCodecModel> contractCodecs)
        => new(
            CreateDtoCodecAnalysisModels(codecs),
            CreateDtoCodecAnalysisModels(contractCodecs));

    private static ImmutableArray<DtoCodecAnalysisModel> CreateDtoCodecAnalysisModels(
        ImmutableArray<GeneratedCodecModel> codecs)
        => codecs
            .Where(static codec => codec.Kind == GeneratedCodecKind.Dto)
            .Select(static codec => new DtoCodecAnalysisModel(
                codec.TypeName,
                codec.CodecName,
                codec.IsReferenceType,
                codec.CodecHashHigh,
                codec.CodecHashLow,
                codec.Members.Select(static member => new DtoMemberAnalysisModel(
                    member.Name,
                    member.Identifier,
                    member.TypeName,
                    member.FieldId,
                    member.Kind,
                    member.FixedTypeName,
                    member.FixedSize,
                    member.Required,
                    member.NonNullableReference,
                    member.InitializerBound)).ToImmutableArray(),
                codec.ConstructorMembers))
            .ToImmutableArray();
}
