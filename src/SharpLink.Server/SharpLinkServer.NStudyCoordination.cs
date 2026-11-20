using System.Reflection;

namespace SharpLink.Server;

internal readonly record struct NStudyServerActivation(SharpLinkAssemblyReplacementResult Replacement,
    NStudyChange? Change, Guid PreparedClientRegistration);

internal sealed partial class SharpLinkServer
{
    internal bool NStudyManualCoordinationEnabled { get; set; }

    internal async Task<NStudyServerActivation> StageNStudyReplacementAsync(
        ServerConnectionState peer,
        Assembly oldAssembly, Assembly newAssembly, long contract, NStudyInitiator initiator,
        Func<NStudyChange, CancellationToken, ValueTask<bool>> approveLocally,
        TimeSpan gracefulTimeout, CancellationToken cancellationToken)
    {
        ValidateReplacementInputs(oldAssembly, newAssembly, gracefulTimeout);
        ArgumentNullException.ThrowIfNull(approveLocally);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connectionRegistry.TryGetValue(peer.Session.Id, out var current) || !ReferenceEquals(current, peer))
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The selected data Session is no longer a server peer.");
        using var ownership = peer.Session.EnterNStudyCoordination();
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(10), _runtimeContext.TimeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timer.Token, peer.ConnectionToken, peer.Session.LifetimeToken);
        var authority = new NStudyAuthority(_desiredSessionServerInstanceId, _nStudyRegistryEpoch, peer.NStudyPhysicalIdentity);
        var coordination = new NStudyServerPreparation(peer, authority, contract, initiator, approveLocally, budget.Token);
        try
        {
            var replacement = await ReplaceAssemblyCoreAsync(oldAssembly, newAssembly, gracefulTimeout,
                cancellationToken, coordination).ConfigureAwait(false);
            if (!replacement.Succeeded && coordination.Change is { } change && !coordination.Declined)
                await peer.Session.SendNStudyCoordinationAsync(new(NStudyPhase.Declined, change), budget.Token).ConfigureAwait(false);
            return new(replacement, coordination.Change, coordination.ClientRegistration);
        }
        catch (Exception error)
        {
            if (coordination.Change.HasValue && !coordination.Activated)
                peer.Session.NotifyDisconnected(error);
            throw;
        }
    }

    // Only the real compatible replacement pipeline can supply this preparation.
    // Remote messages have no assembly references or means to install code.
    private sealed class NStudyServerPreparation(
        ServerConnectionState peer, NStudyAuthority authority, long contract, NStudyInitiator initiator,
        Func<NStudyChange, CancellationToken, ValueTask<bool>> approveLocally, CancellationToken controlToken)
    {
        internal NStudyChange? Change { get; private set; }
        internal Guid ClientRegistration { get; private set; }
        internal bool Declined { get; private set; }
        internal bool Activated { get; private set; }

        internal bool CanCommit =>
            !controlToken.IsCancellationRequested &&
            peer.LifecycleState == ServerConnectionLifecycleState.Ready && peer.Session.CanAcceptCalls &&
            peer.AuthenticationContext is { } authenticated && !authenticated.IsExpired();

        internal bool TryStage(FrozenDictionary<long, ServiceRegistration> current,
            FrozenDictionary<long, ServiceRegistration> candidate,
            SharpLinkDynamicModule oldModule, SharpLinkDynamicModule newModule)
        {
            if (!CanCommit || !current.TryGetValue(contract, out var before) ||
                !ReferenceEquals(before.Module, oldModule) || !candidate.TryGetValue(contract, out var after) ||
                !ReferenceEquals(after.Module, newModule))
                return false;

            // A compatible staged group must replace the same set of service
            // contracts before any announcement or local policy decision.
            var count = 0;
            foreach (var (id, service) in current)
            {
                if (!ReferenceEquals(service.Module, oldModule))
                    continue;
                if (!candidate.TryGetValue(id, out var next) || !ReferenceEquals(next.Module, newModule) ||
                    next.ContractType != service.ContractType || next.NStudyContractHash != service.NStudyContractHash)
                    return false;
                count++;
            }
            if (candidate.Values.Count(service => ReferenceEquals(service.Module, newModule)) != count)
                return false;

            var change = new NStudyChange(peer.Session.NextNStudyTransitionId(), initiator, authority,
                contract, before.NStudyContractHash, before.NStudyTarget, after.NStudyTarget);
            Change = change;
            peer.Session.BlockNStudyTargetBind(change);
            return true;
        }

        internal async ValueTask<bool> NegotiateAsync()
        {
            var change = Change!.Value;
            var session = peer.Session;
            await session.SendNStudyCoordinationAsync(new(NStudyPhase.Candidate, change), controlToken).ConfigureAwait(false);
            if (initiator == NStudyInitiator.Client)
                await session.ReadNStudyCoordinationAsync(change, NStudyPhase.Propose, controlToken).ConfigureAwait(false);
            else
                await session.SendNStudyCoordinationAsync(new(NStudyPhase.Propose, change), controlToken).ConfigureAwait(false);

            if (!await approveLocally(change, controlToken).AsTask().WaitAsync(controlToken).ConfigureAwait(false) || !CanCommit)
            {
                await session.SendNStudyCoordinationAsync(new(NStudyPhase.Declined, change), controlToken).ConfigureAwait(false);
                Declined = true;
                return false;
            }
            await session.SendNStudyCoordinationAsync(new(NStudyPhase.Prepare, change), controlToken).ConfigureAwait(false);
            var ready = await session.ReadNStudyCoordinationAsync(change, NStudyPhase.Ready, controlToken,
                allowDecline: true).ConfigureAwait(false);
            if (ready.Phase == NStudyPhase.Declined)
            {
                Declined = true;
                return false;
            }
            ClientRegistration = ready.ClientRegistration;
            return true;
        }

        internal async ValueTask PublishActivationAsync()
        {
            await peer.Session.SendNStudyCoordinationAsync(new(NStudyPhase.Activated, Change!.Value),
                controlToken).ConfigureAwait(false);
            Activated = true;
        }
    }
}
