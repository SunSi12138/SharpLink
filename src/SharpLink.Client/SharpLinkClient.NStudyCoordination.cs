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
        var session = ready[0].Session;
        using var ownership = session.EnterNStudyCoordination();
        session.ValidateNStudyLocalDecision(approveLocally);
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(10), _runtimeContext.TimeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token, session.LifetimeToken);
        var ct = budget.Token;

        try
        {
            var availability = await session.ReadNStudyCoordinationAsync(ct).ConfigureAwait(false);
            if (availability.Phase != NStudyPhase.Candidate)
                throw RpcSession.InvalidNStudy("A version transition must first declare an actual prepared candidate.");
            var change = availability.Change;
            session.ObserveNStudyTransitionId(change.Id);
            session.ValidateNStudySource(change);
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

            var registration = FindNStudyCompatibleLocalRegistration(change.Contract);
            if (registration?.Module is not { } module ||
                !module.TryAcquire(stream: false, out var localLease))
            {
                await session.SendNStudyCoordinationAsync(new(NStudyPhase.Declined, change), ct).ConfigureAwait(false);
                return null;
            }
            using (localLease)
            {
                // Local generated assets must actually exist. Remote approval cannot
                // install them, and the local registry is rechecked after policy awaits.
                if (module.Manifest.RpcAssemblyHash != change.Hash ||
                    !await session.DecideNStudyLocallyAsync(change, approveLocally, ct).ConfigureAwait(false) ||
                    !IsStillLocalNStudyRegistration(change.Contract, registration))
                {
                    await session.SendNStudyCoordinationAsync(new(NStudyPhase.Declined, change), ct).ConfigureAwait(false);
                    return null;
                }
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
                if (binding.Status != NStudyBindStatus.Bound || binding.Target != change.Target)
                    throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The exact activated target is no longer bindable.");
                return new(change, binding, module.NStudyRegistration);
            }
        }
        catch (Exception error)
        {
            // Ambiguous control completion cannot reopen a frozen route.
            session.NotifyDisconnected(error);
            throw;
        }
    }

    private ClientProxyRegistration? FindNStudyCompatibleLocalRegistration(long contract)
        => _assemblyRegistry.CaptureProxySnapshot().Values.SingleOrDefault(item => item.Descriptor.ContractId == contract);

    private bool IsStillLocalNStudyRegistration(long contract, ClientProxyRegistration registration) =>
        ReferenceEquals(FindNStudyCompatibleLocalRegistration(contract), registration) &&
        registration.Module?.State == SharpLinkDynamicModuleState.Running;
}
