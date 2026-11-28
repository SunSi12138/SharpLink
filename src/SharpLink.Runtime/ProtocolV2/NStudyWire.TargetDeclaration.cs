namespace SharpLink.Runtime;

internal readonly record struct NStudyTargetDeclaration(long Contract, RpcHash128 Hash, NStudyTarget Local);
internal enum NStudyTargetStatus : byte { Missing, Incompatible, Disabled, ApprovalRequired, NotReady, Observed }
internal readonly record struct NStudyTargetDeclarationReply(
    NStudyTargetDeclaration Declaration, NStudyAuthority Authority, NStudyTargetStatus Status, NStudyTarget Target);

internal static partial class NStudyWire
{
    // Optional, private N study extension, not a public Protocol v2 assignment.
    internal const ProtocolV2Capabilities TargetDeclarationCapability = (ProtocolV2Capabilities)(1UL << 8);
    internal const ProtocolV2FrameType TargetDeclaration = (ProtocolV2FrameType)253;
    internal const ProtocolV2FrameType TargetDeclarationReply = (ProtocolV2FrameType)254;
    private const int TargetDeclarationBytes = 48;
    private const int TargetDeclarationReplyBytes = 121;

    internal static void WriteTargetDeclaration(IBufferWriter<byte> writer, NStudyTargetDeclaration declaration)
    {
        WriteTargetDeclaration(writer.GetSpan(TargetDeclarationBytes), declaration);
        writer.Advance(TargetDeclarationBytes);
    }

    internal static NStudyTargetDeclaration ReadTargetDeclaration(ReadOnlySequence<byte> payload)
    {
        Span<byte> bytes = stackalloc byte[TargetDeclarationBytes];
        ReadFixed(payload, bytes);
        return ReadTargetDeclaration(bytes);
    }

    internal static void WriteTargetDeclarationReply(IBufferWriter<byte> writer, NStudyTargetDeclarationReply reply)
    {
        ValidateTargetReply(reply.Status, reply.Target);
        var bytes = writer.GetSpan(TargetDeclarationReplyBytes)[..TargetDeclarationReplyBytes];
        WriteTargetDeclaration(bytes, reply.Declaration);
        WriteInventoryAuthority(bytes[48..96], reply.Authority);
        bytes[96] = (byte)reply.Status;
        WriteTarget(bytes[97..], reply.Target);
        writer.Advance(bytes.Length);
    }

    internal static NStudyTargetDeclarationReply ReadTargetDeclarationReply(ReadOnlySequence<byte> payload)
    {
        Span<byte> bytes = stackalloc byte[TargetDeclarationReplyBytes];
        ReadFixed(payload, bytes);
        var declaration = ReadTargetDeclaration(bytes);
        var authority = ReadInventoryAuthority(bytes[48..96]);
        var status = (NStudyTargetStatus)bytes[96];
        var target = ReadTarget(bytes[97..]);
        ValidateTargetReply(status, target);
        return new(declaration, authority, status, target);
    }

    private static void WriteTargetDeclaration(Span<byte> bytes, NStudyTargetDeclaration declaration)
    {
        ValidateContract(declaration.Contract, declaration.Hash);
        ValidateLocalTarget(declaration.Local);
        WriteContract(bytes, declaration.Contract, declaration.Hash);
        WriteTarget(bytes[24..], declaration.Local);
    }

    private static NStudyTargetDeclaration ReadTargetDeclaration(ReadOnlySpan<byte> bytes)
    {
        var (contract, hash) = ReadContract(bytes);
        var local = ReadTarget(bytes[24..]);
        ValidateLocalTarget(local);
        return new(contract, hash, local);
    }

    private static void ValidateLocalTarget(NStudyTarget local)
    {
        ValidateTarget(local);
        if (local == default)
            throw Invalid("A target declaration must name an actual local registration.");
    }

    private static void ValidateTargetReply(NStudyTargetStatus status, NStudyTarget target)
    {
        ValidateTarget(target);
        if (status > NStudyTargetStatus.Observed || (status == NStudyTargetStatus.Observed) != (target != default))
            throw Invalid("A target declaration reply may only identify an observed exact registration.");
    }
}
