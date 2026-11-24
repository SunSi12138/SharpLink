namespace SharpLink.Client;

internal readonly record struct NStudyClientActivation(NStudyChange Change, NStudyAck Binding, Guid LocalRegistration);

internal sealed partial class SharpLinkClient
{
    private readonly record struct NStudyClientOutcome(
        NStudyChange Change, NStudyClientActivation? Activation, NStudyDeclineReason DeclineReason);

    private volatile NStudyCoordinationMode _nStudyVersionCoordinationMode;
    internal NStudyCoordinationMode NStudyVersionCoordinationMode
    {
        get => _nStudyVersionCoordinationMode;
        set => _nStudyVersionCoordinationMode = value;
    }

    internal NStudyVersionObservationQueue ConfigureNStudyVersionObserver(
        Func<NStudyVersionNotice, CancellationToken, ValueTask> observer)
        => _runtimeContext.ConfigureNStudyVersionObserver(observer, _logger);

    internal async Task<NStudyClientActivation?> NegotiateNStudyReplacementAsync(
        NStudyInitiator localChoice,
        Func<NStudyChange, CancellationToken, ValueTask<bool>>? approveLocally,
        CancellationToken cancellationToken)
        => (await NegotiateNStudyReplacementCoreAsync(localChoice, NStudyVersionProjection.AdaptApproval(approveLocally),
            null, null, cancellationToken)
            .ConfigureAwait(false)).Activation;

    internal async ValueTask<SharpLinkClientVersionChangeResult> JoinVersionChangeAsync(
        RpcLocalContract local, SharpLinkVersionParticipant participant,
        Func<SharpLinkVersionChange, CancellationToken, ValueTask<SharpLinkVersionDecision>>? approveLocally,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(local);
        cancellationToken.ThrowIfCancellationRequested();
        var registration = _assemblyRegistry.ResolveNStudyLocalContract(local);
        if (registration.Module is null)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                "Version coordination requires an installed dynamic contract.");
        var outcome = await NegotiateNStudyReplacementCoreAsync(
            NStudyVersionProjection.ToInitiator(participant.Initiator),
            NStudyVersionProjection.ToApproval(approveLocally), registration, participant.Session, cancellationToken)
            .ConfigureAwait(false);
        return new(NStudyVersionProjection.ToChange(outcome.Change),
            NStudyVersionProjection.ToDeclineReason(outcome.DeclineReason), outcome.Activation?.LocalRegistration);
    }

    private async Task<NStudyClientOutcome> NegotiateNStudyReplacementCoreAsync(
        NStudyInitiator localChoice,
        Func<NStudyChange, CancellationToken, ValueTask<SharpLinkVersionDecision>>? approveLocally,
        ClientProxyRegistration? expectedRegistration, SharpLinkVersionSession? expectedSession,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mode = NStudyVersionCoordinationMode;
        if (expectedSession.HasValue && mode == NStudyCoordinationMode.Disabled)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Local policy disabled version coordination.");
        var connection = expectedSession is { } peer ? RequireVersionConnection(peer) : RequireSingleNStudyConnection();
        var session = connection.Session;
        using var ownership = session.EnterNStudyCoordination(mode, localChoice, approveLocally,
            requirePolicyDeferral: expectedSession.HasValue);
        var clock = _runtimeContext.TimeProvider;
        var controlDeadline = RpcDeadline.Create(NStudyApprovalScheduler.ControlTimeout, clock);
        using var timer = new CancellationTokenSource(controlDeadline.GetRemaining(clock), clock);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token, session.LifetimeToken);
        var ct = budget.Token;
        NStudyContractPreparation? reserved = null;
        NStudyChange? observedChange = null;
        var observation = NStudyVersionNoticeKind.Failed;
        var declineReason = NStudyDeclineReason.None;

        try
        {
            var availability = await session.ReadNStudyCoordinationAsync(ct).ConfigureAwait(false);
            if (availability.Phase == NStudyPhase.Declined && session.SupportsNStudyOfferAdmission &&
                ownership.MatchesAdmittedOffer(availability.Change))
            {
                observedChange = availability.Change;
                return Declined(availability.Change, availability.DeclineReason);
            }
            if (availability.Phase != NStudyPhase.Candidate)
                throw RpcSession.InvalidNStudy("A version transition must first declare an actual prepared candidate.");
            var change = availability.Change;
            if (change.Initiator != localChoice)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "The announced candidate has a different initiator.");
            observedChange = change;
            _runtimeContext.PublishNStudyVersionNotice(new(NStudyVersionNoticeKind.Proposed, change, false));
            if (localChoice == NStudyInitiator.Client)
                await session.SendNStudyCoordinationAsync(new(NStudyPhase.Propose, change), ct).ConfigureAwait(false);
            else
                await session.ReadNStudyCoordinationAsync(change, NStudyPhase.Propose, ct).ConfigureAwait(false);

            var prepared = await session.ReadNStudyCoordinationAsync(change, NStudyPhase.Prepare,
                ct, allowDecline: true).ConfigureAwait(false);
            if (prepared.Phase == NStudyPhase.Declined)
                return Declined(change, prepared.DeclineReason);
            // A negotiated Session may finish a paired exchange after the local
            // application switched off approvals. Decline before acquiring assets or freezing R1.
            if (ownership.Mode == NStudyCoordinationMode.Disabled)
                return await RefuseAsync(change, NStudyDeclineReason.Policy).ConfigureAwait(false);
            if (session.ReadNStudyCandidateSource(change) is not { } source)
                return await RefuseAsync(change, NStudyDeclineReason.SourceUnavailable).ConfigureAwait(false);
            var sourceReceipt = source.Acknowledgement;

            var registration = FindNStudyCompatibleLocalRegistration(change.Contract);
            if (expectedRegistration is not null && !ReferenceEquals(expectedRegistration, registration))
                return await RefuseAsync(change, NStudyDeclineReason.LocalAssetMissing).ConfigureAwait(false);
            if (registration?.Module is not { } module ||
                !module.TryAcquire(stream: false, out var localLease))
                return await RefuseAsync(change, NStudyDeclineReason.LocalAssetMissing).ConfigureAwait(false);
            using (localLease)
            {
                // Local generated assets must actually exist. Remote approval cannot
                // install them, and the local registry is rechecked after policy awaits.
                if (module.Manifest.RpcAssemblyHash != change.Hash)
                    return await RefuseAsync(change, NStudyDeclineReason.LocalAssetIncompatible).ConfigureAwait(false);
                var decision = await ownership.DecideAsync(change, _logger, controlDeadline, ct).ConfigureAwait(false);
                if (decision != SharpLinkVersionDecision.Approve)
                    return await RefuseAsync(change, NStudyVersionProjection.FromNonApproval(decision)).ConfigureAwait(false);
                if (!IsStillLocalNStudyRegistration(change.Contract, registration))
                    return await RefuseAsync(change, NStudyDeclineReason.LocalAssetMissing).ConfigureAwait(false);
                var preparation = registration.NStudyPreparation.Get();
                var currentConnections = CaptureNStudyReadyConnections();
                var soleReadyConnection = currentConnections.Length == 1 && ReferenceEquals(currentConnections[0], connection);
                bool establishPin;
                lock (preparation.Gate)
                {
                    if (preparation.TryReserveResume(connection, sourceReceipt, soleReadyConnection, out establishPin))
                        reserved = preparation;
                }
                if (reserved is null)
                    return await RefuseAsync(change, NStudyDeclineReason.SourceUnavailable).ConfigureAwait(false);
                await session.FreezeNStudySource(change).WaitAsync(ct).ConfigureAwait(false);
                await session.SendNStudyCoordinationAsync(new(NStudyPhase.Ready, change, module.NStudyRegistration),
                    ct).ConfigureAwait(false);
                var activated = await session.ReadNStudyCoordinationAsync(change, NStudyPhase.Activated,
                    ct, allowDecline: true).ConfigureAwait(false);
                if (activated.Phase == NStudyPhase.Declined)
                    return Declined(change, activated.DeclineReason);
                if (!IsStillLocalNStudyRegistration(change.Contract, registration))
                    throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The prepared local contract changed before activation.");
                var binding = await session.PrepareNStudyBindAsync(new(change.Contract, change.Hash, change.Target),
                    ct, resuming: change).ConfigureAwait(false);
                if (binding.Status != NStudyBindStatus.Bound || binding.Target != change.Target ||
                    binding.Authority != change.Authority)
                    throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The exact activated target is no longer bindable.");
                var route = session.CaptureNStudyRoute(change.Contract);
                lock (preparation.Gate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!IsStillLocalNStudyRegistration(change.Contract, registration) ||
                        !IsNStudyPreparationConnectionCurrent(connection) ||
                        route.Acknowledgement != binding || !route.IsOpenOn(session))
                        throw NStudyPreparedSite.Unavailable();
                    preparation.PublishResume(connection, sourceReceipt, new(connection, route, binding), establishPin);
                }
                observation = NStudyVersionNoticeKind.Completed;
                return new(change, new(change, binding, module.NStudyRegistration), NStudyDeclineReason.None);
            }
        }
        catch (Exception error)
        {
            // Check the original lifetime before abort cleanup can disconnect the Session.
            var endedSession = expectedSession.HasValue && error is OperationCanceledException &&
                !cancellationToken.IsCancellationRequested && !timer.IsCancellationRequested &&
                session.LifetimeToken.IsCancellationRequested;
            // Before accepting an offer there is no ambiguous remote transition to unwind.
            // Atomically close that enrollment so a racing offer cannot claim an exiting scope.
            if (!session.SupportsNStudyOfferAdmission || ownership.CloseEnrollmentForAbort() ||
                error is not OperationCanceledException)
                session.NotifyDisconnected(error);
            if (endedSession)
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                    "The selected connection ended during version coordination.", error);
            throw;
        }
        finally
        {
            if (reserved is not null)
            {
                lock (reserved.Gate)
                    reserved.ReleaseResume(connection);
            }
            if (observedChange is { } change)
            {
                _runtimeContext.PublishNStudyVersionNotice(new(observation, change,
                    observation == NStudyVersionNoticeKind.Completed, declineReason));
            }
        }

        NStudyClientOutcome Declined(NStudyChange change, NStudyDeclineReason reason)
        {
            observation = NStudyVersionNoticeKind.Declined;
            declineReason = reason;
            return new(change, null, reason);
        }

        async ValueTask<NStudyClientOutcome> RefuseAsync(NStudyChange change, NStudyDeclineReason reason)
        {
            await session.SendNStudyCoordinationAsync(new(NStudyPhase.Declined, change, DeclineReason: reason),
                ct).ConfigureAwait(false);
            return Declined(change, reason);
        }
    }

    private ClientProxyRegistration? FindNStudyCompatibleLocalRegistration(long contract)
        => _assemblyRegistry.CaptureProxySnapshot().Values.SingleOrDefault(item => item.Descriptor.ContractId == contract);

    private bool IsStillLocalNStudyRegistration(long contract, ClientProxyRegistration registration) =>
        ReferenceEquals(FindNStudyCompatibleLocalRegistration(contract), registration) &&
        registration.Module?.State == SharpLinkDynamicModuleState.Running;
}
