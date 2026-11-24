namespace SharpLink.Runtime;

internal static class NStudyVersionProjection
{
    internal static NStudyCoordinationMode ToMode(SharpLinkVersionCoordinationMode mode)
        => mode switch
        {
            SharpLinkVersionCoordinationMode.Disabled => NStudyCoordinationMode.Disabled,
            SharpLinkVersionCoordinationMode.Manual => NStudyCoordinationMode.Manual,
            SharpLinkVersionCoordinationMode.Automatic => NStudyCoordinationMode.Automatic,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    internal static NStudyInitiator ToInitiator(SharpLinkVersionInitiator initiator)
        => initiator switch
        {
            SharpLinkVersionInitiator.Client => NStudyInitiator.Client,
            SharpLinkVersionInitiator.Server => NStudyInitiator.Server,
            _ => throw new ArgumentOutOfRangeException(nameof(initiator))
        };

    internal static Func<NStudyChange, CancellationToken, ValueTask<bool>>? ToApproval(
        Func<SharpLinkVersionChange, CancellationToken, ValueTask<bool>>? approval)
        => approval is null ? null : (change, ct) => approval(ToChange(change), ct);

    internal static Func<NStudyVersionNotice, CancellationToken, ValueTask> ToObserver(
        Func<SharpLinkVersionChangeNotice, CancellationToken, ValueTask> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return (notice, ct) => observer(new(ToStage(notice.Kind), ToChange(notice.Change),
            notice.LocalPublicationOccurred, ToDeclineReason(notice.DeclineReason)), ct);
    }

    internal static SharpLinkVersionChange ToChange(NStudyChange change)
        => new(change.Id, NStudyInventorySnapshot.ToVersionSession(change.Authority),
            change.Initiator == NStudyInitiator.Client ? SharpLinkVersionInitiator.Client : SharpLinkVersionInitiator.Server,
            ToRegistration(change.Contract, change.Hash, change.Original),
            ToRegistration(change.Contract, change.Hash, change.Target));

    internal static SharpLinkVersionRegistration ToRegistration(long contract, RpcHash128 hash, NStudyTarget target)
        => new(contract, hash, target.Registration, target.Publication);

    internal static SharpLinkVersionDeclineReason ToDeclineReason(NStudyDeclineReason reason)
        => reason switch
        {
            NStudyDeclineReason.None => SharpLinkVersionDeclineReason.None,
            NStudyDeclineReason.Policy => SharpLinkVersionDeclineReason.Policy,
            NStudyDeclineReason.LocalAssetMissing => SharpLinkVersionDeclineReason.MissingLocalRegistration,
            NStudyDeclineReason.LocalAssetIncompatible => SharpLinkVersionDeclineReason.IncompatibleLocalRegistration,
            NStudyDeclineReason.SourceUnavailable => SharpLinkVersionDeclineReason.SourceUnavailable,
            NStudyDeclineReason.CandidateChanged => SharpLinkVersionDeclineReason.CandidateChanged,
            NStudyDeclineReason.PeerDisabled => SharpLinkVersionDeclineReason.PeerDisabled,
            NStudyDeclineReason.ApprovalRequired => SharpLinkVersionDeclineReason.ApprovalRequired,
            NStudyDeclineReason.PeerNotEnrolled => SharpLinkVersionDeclineReason.PeerNotEnrolled,
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };

    internal static SharpLinkVersionDeclarationResult ToDeclaration(NStudyTargetDeclarationReply reply)
    {
        var declaration = reply.Declaration;
        var status = reply.Status switch
        {
            NStudyTargetStatus.Missing => SharpLinkVersionDeclarationStatus.Missing,
            NStudyTargetStatus.Incompatible => SharpLinkVersionDeclarationStatus.Incompatible,
            NStudyTargetStatus.Disabled => SharpLinkVersionDeclarationStatus.Disabled,
            NStudyTargetStatus.ApprovalRequired => SharpLinkVersionDeclarationStatus.ApprovalRequired,
            NStudyTargetStatus.NotReady => SharpLinkVersionDeclarationStatus.NotReady,
            NStudyTargetStatus.Observed => SharpLinkVersionDeclarationStatus.Observed,
            _ => throw new ArgumentOutOfRangeException(nameof(reply))
        };
        return new(NStudyInventorySnapshot.ToVersionSession(reply.Authority),
            ToRegistration(declaration.Contract, declaration.Hash, declaration.Local), status,
            status == SharpLinkVersionDeclarationStatus.Observed
                ? ToRegistration(declaration.Contract, declaration.Hash, reply.Target)
                : null);
    }

    private static SharpLinkVersionChangeStage ToStage(NStudyVersionNoticeKind kind)
        => kind switch
        {
            NStudyVersionNoticeKind.Proposed => SharpLinkVersionChangeStage.Proposed,
            NStudyVersionNoticeKind.Published => SharpLinkVersionChangeStage.Published,
            NStudyVersionNoticeKind.Completed => SharpLinkVersionChangeStage.Completed,
            NStudyVersionNoticeKind.Declined => SharpLinkVersionChangeStage.Declined,
            NStudyVersionNoticeKind.Failed => SharpLinkVersionChangeStage.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}
