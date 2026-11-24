using System.Reflection;

namespace SharpLink.Server;

internal readonly record struct NStudyPeerOffer(ServerConnectionState Peer, NStudyInitiator Initiator,
    Func<NStudyChange, CancellationToken, ValueTask<bool>>? ApproveLocally);
internal readonly record struct NStudyPeerActivation(NStudyChange? Change, Guid ClientRegistration,
    NStudyDeclineReason DeclineReason, bool ActivationSent, bool Terminated);
internal readonly record struct NStudyCohortActivation(SharpLinkAssemblyReplacementResult Replacement,
    IReadOnlyList<NStudyPeerActivation> Peers);
internal readonly record struct NStudyServerActivation(SharpLinkAssemblyReplacementResult Replacement,
    NStudyChange? Change, Guid PreparedClientRegistration, NStudyDeclineReason DeclineReason);

internal sealed partial class SharpLinkServer
{
    internal NStudyCoordinationMode NStudyVersionCoordinationMode { get; set; }

    internal async Task<NStudyServerActivation> StageNStudyReplacementAsync(
        ServerConnectionState peer,
        Assembly oldAssembly, Assembly newAssembly, long contract, NStudyInitiator initiator,
        Func<NStudyChange, CancellationToken, ValueTask<bool>>? approveLocally,
        TimeSpan gracefulTimeout, CancellationToken cancellationToken)
    {
        var result = await StageNStudyReplacementOnSessionsAsync([new(peer, initiator, approveLocally)],
            oldAssembly, newAssembly, contract, gracefulTimeout, cancellationToken).ConfigureAwait(false);
        var single = result.Peers[0];
        return new(result.Replacement, single.Change, single.ClientRegistration, single.DeclineReason);
    }

    internal async Task<NStudyCohortActivation> StageNStudyReplacementOnSessionsAsync(
        IReadOnlyList<NStudyPeerOffer> offers, Assembly oldAssembly, Assembly newAssembly, long contract,
        TimeSpan gracefulTimeout, CancellationToken cancellationToken)
    {
        ValidateReplacementInputs(oldAssembly, newAssembly, gracefulTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThan(offers.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offers.Count, 8);
        cancellationToken.ThrowIfCancellationRequested();
        using var controlTimer = new CancellationTokenSource(TimeSpan.FromSeconds(10), _runtimeContext.TimeProvider);
        using var controlBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, controlTimer.Token);
        var participants = new List<NStudyParticipant>(offers.Count);
        NStudyServerPreparation? preparation = null;
        try
        {
            var seen = new HashSet<ServerConnectionState>();
            foreach (var offer in offers)
            {
                var peer = offer.Peer;
                if (!seen.Add(peer))
                    throw new ArgumentException("A physical Session can participate only once.", nameof(offers));
                if (!_connectionRegistry.TryGetValue(peer.Session.Id, out var current) || !ReferenceEquals(current, peer))
                    throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The selected data Session is no longer a server peer.");
                var authority = ReadNStudySessionAuthority(peer);
                participants.Add(NStudyParticipant.Acquire(offer, authority, _logger, controlBudget.Token));
            }
            // The application names the first peer as the commit authorizer.
            // The other selected data Sessions authorize only their own resume.
            preparation = new(participants, seen, _connectionRegistry, contract);
            var replacement = await ReplaceAssemblyCoreAsync(oldAssembly, newAssembly, gracefulTimeout,
                cancellationToken, preparation).ConfigureAwait(false);
            if (!replacement.Succeeded)
                await preparation.AbortBeforeCommitAsync().ConfigureAwait(false);
            return new(replacement, participants.Select(static participant => participant.Outcome).ToArray());
        }
        finally
        {
            if (preparation is not null && !preparation.Published)
                await preparation.AbortBeforeCommitAsync().ConfigureAwait(false);
            foreach (var participant in participants)
                participant.Dispose();
        }
    }

    private sealed class NStudyServerPreparation(
        IReadOnlyList<NStudyParticipant> peers, HashSet<ServerConnectionState> enrolled,
        ServerConnectionRegistry registry, long contract)
    {
        private NStudyTarget _original;
        private bool _aborted;
        internal bool Published { get; private set; }

        internal bool CanCommit => peers[0].CanContinue && IsCohortStillComplete();

        private bool IsCohortStillComplete() => registry.SnapshotActive().All(peer =>
            !peer.Session.SupportsNStudyCoordination || enrolled.Contains(peer) ||
            !peer.NStudyBindings.HasOpenTarget(_original));

        internal bool TryStage(FrozenDictionary<long, ServiceRegistration> current,
            FrozenDictionary<long, ServiceRegistration> candidate,
            SharpLinkDynamicModule oldModule, SharpLinkDynamicModule newModule)
        {
            if (!current.TryGetValue(contract, out var before) ||
                !ReferenceEquals(before.Module, oldModule) || !candidate.TryGetValue(contract, out var after) ||
                !ReferenceEquals(after.Module, newModule))
                return false;
            _original = before.NStudyTarget;
            if (!IsCohortStillComplete() || peers.Any(static peer => !peer.CanContinue))
                return false;
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
            foreach (var peer in peers)
                peer.Stage(contract, before.NStudyContractHash, _original, after.NStudyTarget);
            return true;
        }

        internal async ValueTask<bool> NegotiateAsync()
        {
            // Candidate is queued ahead of any committed invalidation on each
            // physical FIFO. Readiness is per Session after this declaration.
            await Task.WhenAll(peers.Select(static peer => peer.DeclareAsync())).ConfigureAwait(false);
            if (peers[0].Terminated)
                return false;
            foreach (var peer in peers)
                peer.StartDecision();
            return await peers[0].IsReadyAsync().ConfigureAwait(false);
        }

        internal void MarkPublished()
        {
            foreach (var peer in peers)
                peer.KeepTargetBlocked();
            Published = true;
        }

        internal Task PublishActivationAsync() =>
            Task.WhenAll(peers.Select(static peer => peer.ActivateWhenReadyAsync()));

        internal async Task AbortBeforeCommitAsync()
        {
            if (_aborted || Published)
                return;
            _aborted = true;
            await Task.WhenAll(peers.Select(static peer => peer.AbortBeforeCommitAsync())).ConfigureAwait(false);
        }
    }

    private sealed class NStudyParticipant(
        NStudyPeerOffer offer, NStudyAuthority authority, ILogger logger, IDisposable scope, CancellationTokenSource budget) : IDisposable
    {
        private RpcSession Session => offer.Peer.Session;
        private CancellationToken ControlToken => budget.Token;
        private NStudyChange? _change;
        private bool _declared;
        private Task<bool>? _decision;
        private Guid _clientRegistration;
        private NStudyDeclineReason _declineReason;
        private bool _activationSent;
        internal bool Terminated { get; private set; }
        internal bool CanContinue => !Terminated && !ControlToken.IsCancellationRequested &&
            offer.Peer.LifecycleState == ServerConnectionLifecycleState.Ready && Session.CanAcceptCalls &&
            offer.Peer.AuthenticationContext is { } authenticated && !authenticated.IsExpired();
        internal NStudyPeerActivation Outcome => new(_change, _clientRegistration, _declineReason,
            _activationSent, Terminated);

        internal static NStudyParticipant Acquire(NStudyPeerOffer offer, NStudyAuthority authority,
            ILogger logger, CancellationToken ct)
        {
            var scope = offer.Peer.Session.EnterNStudyCoordination();
            try
            {
                offer.Peer.Session.ValidateNStudyLocalDecision(offer.ApproveLocally);
                var budget = CancellationTokenSource.CreateLinkedTokenSource(ct,
                    offer.Peer.ConnectionToken, offer.Peer.Session.LifetimeToken);
                return new(offer, authority, logger, scope, budget);
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        internal void Stage(long contract, RpcHash128 hash, NStudyTarget original, NStudyTarget target)
        {
            var change = new NStudyChange(Session.NextNStudyTransitionId(), offer.Initiator, authority, contract, hash, original, target);
            Session.BlockNStudyTargetBind(change);
            _change = change;
        }

        internal async Task DeclareAsync()
        {
            try
            {
                await SendAsync(NStudyPhase.Candidate).ConfigureAwait(false);
                _declared = true;
            }
            catch (Exception error) { Terminate(error); }
        }

        internal void StartDecision()
        {
            if (_declared && !Terminated)
                _decision = Task.Run(DecideAsync);
        }

        internal Task<bool> IsReadyAsync() => _decision ?? Task.FromResult(false);

        private async Task<bool> DecideAsync()
        {
            try
            {
                var change = _change!.Value;
                if (offer.Initiator == NStudyInitiator.Client)
                    await Session.ReadNStudyCoordinationAsync(change, NStudyPhase.Propose, ControlToken).ConfigureAwait(false);
                else
                    await SendAsync(NStudyPhase.Propose).ConfigureAwait(false);

                if (!await Session.DecideNStudyLocallyAsync(change, offer.ApproveLocally, logger, ControlToken).ConfigureAwait(false))
                {
                    await DeclineAsync(NStudyDeclineReason.Policy).ConfigureAwait(false);
                    return false;
                }
                if (!CanContinue)
                {
                    await DeclineAsync(NStudyDeclineReason.CandidateChanged).ConfigureAwait(false);
                    return false;
                }
                await SendAsync(NStudyPhase.Prepare).ConfigureAwait(false);
                var reply = await Session.ReadNStudyCoordinationAsync(change, NStudyPhase.Ready, ControlToken,
                    allowDecline: true).ConfigureAwait(false);
                _declineReason = reply.DeclineReason;
                if (reply.Phase != NStudyPhase.Ready)
                    return false;

                // The query supplies a fact, not permission: both policies and Ready
                // already ran on this physical Session. Never substitute a different
                // installed generation even if its contract hash is the same.
                var inventory = await Session.QueryNStudyClientInventoryAsync(authority, ControlToken).ConfigureAwait(false);
                if (!CanContinue || inventory.Status != NStudyInventoryStatus.Current ||
                    !inventory.Entries.Any(entry => entry.Contract == change.Contract && entry.Hash == change.Hash &&
                        entry.Target.Registration == reply.ClientRegistration))
                {
                    await DeclineAsync(NStudyDeclineReason.CandidateChanged).ConfigureAwait(false);
                    return false;
                }
                _clientRegistration = reply.ClientRegistration;
                return true;
            }
            catch (Exception error)
            {
                Terminate(error);
                return false;
            }
        }

        internal void KeepTargetBlocked() => Session.KeepNStudyTargetBlockUntilActivated(_change!.Value);

        internal async Task ActivateWhenReadyAsync()
        {
            if (!await IsReadyAsync().ConfigureAwait(false))
                return;
            try
            {
                if (!CanContinue)
                {
                    await DeclineAsync(NStudyDeclineReason.CandidateChanged).ConfigureAwait(false);
                    return;
                }
                await SendAsync(NStudyPhase.Activated).ConfigureAwait(false);
                _activationSent = true;
            }
            catch (Exception error) { Terminate(error); }
        }

        internal async Task AbortBeforeCommitAsync()
        {
            if (!_declared)
                return;
            if (Terminated)
            {
                await IsReadyAsync().ConfigureAwait(false);
                return;
            }
            if (_decision is { IsCompletedSuccessfully: true } decision)
            {
                if (decision.Result)
                {
                    try { await DeclineAsync(NStudyDeclineReason.CandidateChanged).ConfigureAwait(false); }
                    catch (Exception error) { Terminate(error); }
                }
                return;
            }
            // A still-running decision may be between protocol phases. Terminate
            // just that attempt's peer rather than guessing a reusable phase.
            Terminate(new SharpLinkException(SharpLinkErrorCode.Unavailable, "The staged cohort did not commit."));
            await IsReadyAsync().ConfigureAwait(false);
        }

        private async ValueTask DeclineAsync(NStudyDeclineReason reason)
        {
            await Session.SendNStudyCoordinationAsync(new(NStudyPhase.Declined, _change!.Value, DeclineReason: reason),
                ControlToken).ConfigureAwait(false);
            _declineReason = reason;
        }

        private ValueTask SendAsync(NStudyPhase phase) =>
            Session.SendNStudyCoordinationAsync(new(phase, _change!.Value), ControlToken);

        private void Terminate(Exception error)
        {
            Terminated = true;
            Session.NotifyDisconnected(error);
        }

        public void Dispose()
        {
            budget.Dispose();
            scope.Dispose();
        }
    }
}
