namespace SharpLink.Client;

internal readonly record struct NStudyClientActivation(NStudyChange Change, NStudyAck Binding, Guid LocalRegistration);

internal sealed partial class SharpLinkClient
{
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
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ready = Volatile.Read(ref _readyConnections);
        if (ready.Length != 1)
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "A private coordination attempt needs one Ready Session.");
        var connection = ready[0];
        var session = connection.Session;
        using var ownership = session.EnterNStudyCoordination(NStudyVersionCoordinationMode, localChoice, approveLocally);
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
                return Declined(availability.DeclineReason);
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
                return Declined(prepared.DeclineReason);
            // A negotiated Session may finish a paired exchange after the local
            // application switched off approvals. Decline before acquiring assets or freezing R1.
            if (ownership.Mode == NStudyCoordinationMode.Disabled)
                return await RefuseAsync(change, NStudyDeclineReason.Policy).ConfigureAwait(false);
            if (!session.HasNStudyCandidateSource(change))
                return await RefuseAsync(change, NStudyDeclineReason.SourceUnavailable).ConfigureAwait(false);

            var registration = FindNStudyCompatibleLocalRegistration(change.Contract);
            if (registration?.Module is not { } module ||
                !module.TryAcquire(stream: false, out var localLease))
                return await RefuseAsync(change, NStudyDeclineReason.LocalAssetMissing).ConfigureAwait(false);
            using (localLease)
            {
                // Local generated assets must actually exist. Remote approval cannot
                // install them, and the local registry is rechecked after policy awaits.
                if (module.Manifest.RpcAssemblyHash != change.Hash)
                    return await RefuseAsync(change, NStudyDeclineReason.LocalAssetIncompatible).ConfigureAwait(false);
                if (!await ownership.DecideAsync(change, _logger, controlDeadline, ct).ConfigureAwait(false))
                    return await RefuseAsync(change, NStudyDeclineReason.Policy).ConfigureAwait(false);
                if (!IsStillLocalNStudyRegistration(change.Contract, registration))
                    return await RefuseAsync(change, NStudyDeclineReason.LocalAssetMissing).ConfigureAwait(false);
                var preparation = registration.NStudyPreparation.Get();
                lock (preparation.Gate)
                {
                    if (preparation.InFlight is null && !preparation.Coordinating)
                    {
                        preparation.Coordinating = true;
                        reserved = preparation;
                    }
                }
                if (reserved is null)
                    return await RefuseAsync(change, NStudyDeclineReason.SourceUnavailable).ConfigureAwait(false);
                await session.FreezeNStudySource(change).WaitAsync(ct).ConfigureAwait(false);
                await session.SendNStudyCoordinationAsync(new(NStudyPhase.Ready, change, module.NStudyRegistration),
                    ct).ConfigureAwait(false);
                var activated = await session.ReadNStudyCoordinationAsync(change, NStudyPhase.Activated,
                    ct, allowDecline: true).ConfigureAwait(false);
                if (activated.Phase == NStudyPhase.Declined)
                    return Declined(activated.DeclineReason);
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
                    // Publish the acknowledged route for the original pinned receiver.
                    // Do not bind again, switch connections, or republish a cohort.
                    Volatile.Write(ref preparation.Ready, new NStudyPreparedSite(new NStudyPreparedRoute(connection, route, binding)));
                }
                observation = NStudyVersionNoticeKind.Completed;
                return new(change, binding, module.NStudyRegistration);
            }
        }
        catch (Exception error)
        {
            // Before accepting an offer there is no ambiguous remote transition to unwind.
            // Atomically close that enrollment so a racing offer cannot claim an exiting scope.
            if (!session.SupportsNStudyOfferAdmission || ownership.CloseEnrollmentForAbort() ||
                error is not OperationCanceledException)
                session.NotifyDisconnected(error);
            throw;
        }
        finally
        {
            if (reserved is not null)
            {
                lock (reserved.Gate)
                    reserved.Coordinating = false;
            }
            if (observedChange is { } change)
            {
                _runtimeContext.PublishNStudyVersionNotice(new(observation, change,
                    observation == NStudyVersionNoticeKind.Completed, declineReason));
            }
        }

        NStudyClientActivation? Declined(NStudyDeclineReason reason)
        {
            observation = NStudyVersionNoticeKind.Declined;
            declineReason = reason;
            return null;
        }

        async ValueTask<NStudyClientActivation?> RefuseAsync(NStudyChange change, NStudyDeclineReason reason)
        {
            await session.SendNStudyCoordinationAsync(new(NStudyPhase.Declined, change, DeclineReason: reason),
                ct).ConfigureAwait(false);
            return Declined(reason);
        }
    }

    private ClientProxyRegistration? FindNStudyCompatibleLocalRegistration(long contract)
        => _assemblyRegistry.CaptureProxySnapshot().Values.SingleOrDefault(item => item.Descriptor.ContractId == contract);

    private bool IsStillLocalNStudyRegistration(long contract, ClientProxyRegistration registration) =>
        ReferenceEquals(FindNStudyCompatibleLocalRegistration(contract), registration) &&
        registration.Module?.State == SharpLinkDynamicModuleState.Running;
}
