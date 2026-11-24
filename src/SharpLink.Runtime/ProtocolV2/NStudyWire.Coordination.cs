namespace SharpLink.Runtime;

internal enum NStudyCoordinationMode : byte { Disabled, Manual, Automatic }
internal enum NStudyInitiator : byte { Client = 1, Server }
internal enum NStudyPhase : byte { Candidate = 1, Propose, Prepare, Ready, Activated, Declined, Offer, Enrolled }
internal enum NStudyDeclineReason : byte
{
    None, Policy, LocalAssetMissing, LocalAssetIncompatible, SourceUnavailable, CandidateChanged,
    PeerDisabled, ApprovalRequired, PeerNotEnrolled, PolicyDeferred
}
internal readonly record struct NStudyChange(
    ulong Id, NStudyInitiator Initiator, NStudyAuthority Authority, long Contract, RpcHash128 Hash,
    NStudyTarget Original, NStudyTarget Target);
internal readonly record struct NStudyCoordinationMessage(NStudyPhase Phase, NStudyChange Change,
    Guid ClientRegistration = default, NStudyDeclineReason DeclineReason = NStudyDeclineReason.None);

internal static partial class NStudyWire
{
    internal const ProtocolV2Capabilities CoordinationCapability = (ProtocolV2Capabilities)(1UL << 7);
    // Private optional extension to the existing fixed-width coordination exchange.
    internal const ProtocolV2Capabilities OfferAdmissionCapability = (ProtocolV2Capabilities)(1UL << 9);
    // The new decision uses the existing Declined phase, with an optional new reason.
    internal const ProtocolV2Capabilities PolicyDeferralCapability = (ProtocolV2Capabilities)(1UL << 10);
    internal const ProtocolV2FrameType Coordination = (ProtocolV2FrameType)250;
    private const int CoordinationBytes = 139;

    internal static void WriteCoordination(IBufferWriter<byte> writer, NStudyCoordinationMessage message)
    {
        ValidateCoordination(message);
        var bytes = writer.GetSpan(CoordinationBytes)[..CoordinationBytes];
        var change = message.Change;
        bytes[0] = (byte)message.Phase;
        bytes[1] = (byte)change.Initiator;
        WriteContract(bytes[2..], change.Contract, change.Hash);
        _ = change.Authority.Server.TryWriteBytes(bytes[26..42]);
        _ = change.Authority.RegistryEpoch.TryWriteBytes(bytes[42..58]);
        _ = change.Authority.PhysicalSession.TryWriteBytes(bytes[58..74]);
        WriteTarget(bytes[74..98], change.Original);
        WriteTarget(bytes[98..122], change.Target);
        _ = message.ClientRegistration.TryWriteBytes(bytes[122..138]);
        bytes[138] = (byte)message.DeclineReason;
        writer.Advance(CoordinationBytes);
    }

    internal static NStudyCoordinationMessage ReadCoordination(ulong id, ReadOnlySequence<byte> payload)
    {
        Span<byte> bytes = stackalloc byte[CoordinationBytes];
        ReadFixed(payload, bytes);
        var (contract, hash) = ReadContract(bytes[2..]);
        var change = new NStudyChange(id, (NStudyInitiator)bytes[1],
            new(new Guid(bytes[26..42]), new Guid(bytes[42..58]), new Guid(bytes[58..74])),
            contract, hash, ReadTarget(bytes[74..98]), ReadTarget(bytes[98..122]));
        var message = new NStudyCoordinationMessage((NStudyPhase)bytes[0], change, new Guid(bytes[122..138]),
            (NStudyDeclineReason)bytes[138]);
        ValidateCoordination(message);
        return message;
    }

    private static void ValidateCoordination(NStudyCoordinationMessage message)
    {
        var change = message.Change;
        ValidateContract(change.Contract, change.Hash);
        ValidateTarget(change.Original);
        ValidateTarget(change.Target);
        if (change.Id == 0 || change.Initiator is not (NStudyInitiator.Client or NStudyInitiator.Server) ||
            message.Phase is < NStudyPhase.Candidate or > NStudyPhase.Enrolled ||
            change.Authority.Server == Guid.Empty || change.Authority.RegistryEpoch == Guid.Empty ||
            change.Authority.PhysicalSession == Guid.Empty ||
            change.Original == default || change.Target == default ||
            change.Original.Registration == change.Target.Registration ||
            change.Target.Publication <= change.Original.Publication ||
            message.DeclineReason > NStudyDeclineReason.PolicyDeferred ||
            (message.Phase == NStudyPhase.Declined) != (message.DeclineReason != NStudyDeclineReason.None) ||
            (message.Phase == NStudyPhase.Ready) != (message.ClientRegistration != Guid.Empty))
            throw Invalid("Invalid private version transition.");
    }
}
