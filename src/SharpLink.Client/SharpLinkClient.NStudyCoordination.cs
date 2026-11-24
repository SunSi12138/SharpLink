namespace SharpLink.Client;

internal readonly record struct NStudyClientActivation(NStudyChange Change, NStudyAck Binding, Guid LocalRegistration);

internal sealed partial class SharpLinkClient
{
    // Sampled at the actual handshake; later setting changes apply to future Sessions only.
    internal NStudyCoordinationMode NStudyVersionCoordinationMode { get; set; }

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
        using var ownership = session.EnterNStudyCoordination();
        session.ValidateNStudyLocalDecision(approveLocally);
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(10), _runtimeContext.TimeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token, session.LifetimeToken);
        var ct = budget.Token;
        NStudyContractPreparation? reserved = null;

        try
        {
            var availability = await session.ReadNStudyCoordinationAsync(ct).ConfigureAwait(false);
            if (availability.Phase != NStudyPhase.Candidate)
                throw RpcSession.InvalidNStudy("A version transition must first declare an actual prepared candidate.");
            var change = availability.Change;
            if (change.Initiator != localChoice)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "The announced candidate has a different initiator.");
            if (localChoice == NStudyInitiator.Client)
                await session.SendNStudyCoordinationAsync(new(NStudyPhase.Propose, change), ct).ConfigureAwait(false);
            else
                await session.ReadNStudyCoordinationAsync(change, NStudyPhase.Propose, ct).ConfigureAwait(false);

            var prepared = await session.ReadNStudyCoordinationAsync(change, NStudyPhase.Prepare,
                ct, allowDecline: true).ConfigureAwait(false);
            if (prepared.Phase == NStudyPhase.Declined)
                return null;
            if (!session.HasNStudyCandidateSource(change))
                return await DeclineNStudyAsync(session, change, NStudyDeclineReason.SourceUnavailable, ct).ConfigureAwait(false);

            var registration = FindNStudyCompatibleLocalRegistration(change.Contract);
            if (registration?.Module is not { } module ||
                !module.TryAcquire(stream: false, out var localLease))
                return await DeclineNStudyAsync(session, change, NStudyDeclineReason.LocalAssetMissing, ct).ConfigureAwait(false);
            using (localLease)
            {
                // Local generated assets must actually exist. Remote approval cannot
                // install them, and the local registry is rechecked after policy awaits.
                if (module.Manifest.RpcAssemblyHash != change.Hash)
                    return await DeclineNStudyAsync(session, change, NStudyDeclineReason.LocalAssetIncompatible, ct).ConfigureAwait(false);
                if (!await session.DecideNStudyLocallyAsync(change, approveLocally, _logger, ct).ConfigureAwait(false))
                    return await DeclineNStudyAsync(session, change, NStudyDeclineReason.Policy, ct).ConfigureAwait(false);
                if (!IsStillLocalNStudyRegistration(change.Contract, registration))
                    return await DeclineNStudyAsync(session, change, NStudyDeclineReason.LocalAssetMissing, ct).ConfigureAwait(false);
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
                    return await DeclineNStudyAsync(session, change, NStudyDeclineReason.SourceUnavailable, ct).ConfigureAwait(false);
                await session.FreezeNStudySource(change).WaitAsync(ct).ConfigureAwait(false);
                await session.SendNStudyCoordinationAsync(new(NStudyPhase.Ready, change, module.NStudyRegistration),
                    ct).ConfigureAwait(false);
                var activated = await session.ReadNStudyCoordinationAsync(change, NStudyPhase.Activated,
                    ct, allowDecline: true).ConfigureAwait(false);
                if (activated.Phase == NStudyPhase.Declined)
                    return null;
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
                return new(change, binding, module.NStudyRegistration);
            }
        }
        catch (Exception error)
        {
            // Ambiguous control completion cannot reopen a frozen route.
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
        }
    }

    private static async ValueTask<NStudyClientActivation?> DeclineNStudyAsync(RpcSession session,
        NStudyChange change, NStudyDeclineReason reason, CancellationToken ct)
    {
        await session.SendNStudyCoordinationAsync(new(NStudyPhase.Declined, change, DeclineReason: reason),
            ct).ConfigureAwait(false);
        return null;
    }

    private ClientProxyRegistration? FindNStudyCompatibleLocalRegistration(long contract)
        => _assemblyRegistry.CaptureProxySnapshot().Values.SingleOrDefault(item => item.Descriptor.ContractId == contract);

    private bool IsStillLocalNStudyRegistration(long contract, ClientProxyRegistration registration) =>
        ReferenceEquals(FindNStudyCompatibleLocalRegistration(contract), registration) &&
        registration.Module?.State == SharpLinkDynamicModuleState.Running;
}
