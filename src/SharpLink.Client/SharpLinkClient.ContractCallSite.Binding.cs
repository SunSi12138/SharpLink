namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal sealed partial class ContractCallSite
    {
        private readonly Lock _bindingGate = new();
        private readonly Dictionary<ClientConnection, BindingAttempt> _bindingAttempts = [];
        private BoundTarget[] _boundTargets = [];
        private TaskCompletionSource<Exception?> _bindingSignal = NewBindingSignal();
        private int _bindingWaiters;
        private bool _demandRetired;
        private readonly CancellationTokenSource _demandRetirement = new();
        internal CancellationToken DemandRetirement => _demandRetirement.Token;

        internal bool HasBoundRoute
        {
            get
            {
                foreach (var target in Volatile.Read(ref _boundTargets))
                    if (target.IsEligible && !LocalControl.IsRetired)
                        return true;
                return false;
            }
        }

        // Only a new explicit cold Prepare may reopen an infrastructure failure after
        // its original candidate cleanup completed. Previously captured notifications
        // keep their immutable error result even after this current signal is replaced.
        internal Task<Exception?> BeginPreparation()
        {
            lock (_bindingGate)
            {
                // A fresh caller can use an already-confirmed route even if a different
                // attempt failed. Existing waiters retain THEIR captured failure instead.
                if (_owner._cluster is null ? HasBoundRoute : _contractRouteView.HasEligibleTarget())
                    return Task.FromResult<Exception?>(null);
                foreach (var pair in _bindingAttempts)
                    if (!pair.Value.Completed && pair.Value.PublicationFailure is { } pending)
                        throw pending;
                var nextSignal = NewBindingSignal();
                foreach (var pair in _bindingAttempts)
                    if (pair.Value.Completed) pair.Value.PublicationFailure = null;
                var oldSignal = _bindingSignal;
                _bindingSignal = nextSignal;
                oldSignal.TrySetResult(null);
                return nextSignal.Task;
            }
        }

        private static void ThrowCapturedPublicationFailure(Task<Exception?> notification)
        {
            if (notification.IsCompletedSuccessfully && notification.GetAwaiter().GetResult() is { } error)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        private Exception? CapturePublicationFailureLocked()
        {
            foreach (var pair in _bindingAttempts)
                if (pair.Value.PublicationFailure is { } error) return error;
            return null;
        }

        internal bool TryCompleteReadyPreparation(TimeSpan timeout, long started, CancellationToken cancellationToken,
            Task<Exception?> notification)
        {
            lock (_bindingGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_owner._runtimeContext.TimeProvider.GetElapsedTime(started) >= timeout)
                    throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Prepare deadline expired.");
                if (_owner._shutdownCts.IsCancellationRequested ||
                    _owner.State is SharpLinkConnectionState.Draining or SharpLinkConnectionState.Stopped)
                    throw CreateConnectionClosedException("Client is stopping or stopped.");
                LocalControl.ThrowIfRetired();
                ThrowCapturedPublicationFailure(notification);
                ReconcileContractRoute();
                if (_owner._cluster is null ? HasBoundRoute : _contractRouteView.HasEligibleTarget()) return true;
                if (CapturePublicationFailureLocked() is { } publicationFailure)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(publicationFailure).Throw();
                return false;
            }
        }

        internal bool TryBeginBinding(ClientConnection connection, bool retryCompletedFailures)
        {
            lock (_bindingGate)
            {
                if (_demandRetired || LocalControl.IsRetired)
                    return false;
                foreach (var stale in _bindingAttempts.Where(static pair => pair.Value.Completed && !pair.Key.Session.IsConnected)
                    .Select(static pair => pair.Key).ToArray())
                    _bindingAttempts.Remove(stale);
                if (_bindingAttempts.TryGetValue(connection, out var attempt))
                {
                    if (!attempt.Completed || attempt.Failure is not null && !retryCompletedFailures &&
                        !attempt.RouteBuildFailed && !IsRetryableBindingCapacityFailure(attempt.Failure))
                        return false; // deterministic failures wait for a cold Session/Manifest/invalidation event
                    if (_boundTargets.Any(target => ReferenceEquals(target.Connection, connection) && target.IsEligible))
                        return false;
                }
                _bindingAttempts[connection] = new BindingAttempt();
                return true;
            }
        }

        internal void PublishBinding(BoundTarget binding)
        {
            var connection = binding.Connection;
            _owner.ContractRouteBuildForTesting?.Invoke(this, "before-bind-lock");
            lock (_bindingGate)
            {
                if (_demandRetired || LocalControl.IsRetired || Volatile.Read(ref _owner._contractRoutesStopped) || !connection.CanAcceptCalls)
                    throw NoBoundRoute();
                var attempt = _bindingAttempts[connection];
                try
                {
                    if (_bindingRevision == ulong.MaxValue)
                    {
                        ExhaustContractRouteRevision();
                        throw RouteRevisionExhausted();
                    }
                    _owner.ContractRouteBuildForTesting?.Invoke(this, "targets");
                    var targets = _boundTargets.Where(target => !ReferenceEquals(target.Connection, connection) && target.IsEligible)
                        .Append(binding).ToArray();
                    var topology = LatestContractTopologyLocked();
                    var view = BuildContractRouteLocked(topology, targets, _bindingRevision + 1);
                    _owner.ContractRouteBuildForTesting?.Invoke(this, "signal");
                    var signal = NewBindingSignal();
                    // Everything which can fail construction is above this point.
                    attempt.Completed = true;
                    // A real successful Bind publication recovers current failure authority;
                    // it cannot erase errors already stored in a captured notification.
                    foreach (var pair in _bindingAttempts) pair.Value.PublicationFailure = null;
                    CommitContractRouteLocked(view, signal);
                }
                catch (Exception error)
                {
                    // Publish the original cold error before physical retirement can wait.
                    // Completed/Failure remain owned by BindingFailed AFTER actual cleanup.
                    attempt.PublicationFailure = error;
                    attempt.RouteBuildFailed = true;
                    _bindingSignal.TrySetResult(error);
                    var failure = new ContractRouteFailure(1);
                    failure.Add("bind-route-build", error);
                    throw failure;
                }
            }
        }

        internal void BindingFailed(ClientConnection connection, Exception exception)
        {
            lock (_bindingGate)
            {
                var nextSignal = NewBindingSignal();
                if (_bindingAttempts.TryGetValue(connection, out var attempt))
                {
                    attempt.Completed = true;
                    attempt.Failure = exception;
                }
                var oldSignal = _bindingSignal;
                _bindingSignal = nextSignal;
                oldSignal.TrySetResult(CapturePublicationFailureLocked());
            }
        }

        internal Exception? CaptureBindingFailure()
        {
            lock (_bindingGate)
            {
                if (CapturePublicationFailureLocked() is { } publicationFailure) return publicationFailure;
                Exception? failure = null;
                foreach (var pair in _bindingAttempts)
                {
                    if (!pair.Key.CanAcceptCalls)
                        continue;
                    if (!pair.Value.Completed || pair.Value.Failure is null)
                        return null;
                    failure ??= pair.Value.Failure;
                }
                return failure;
            }
        }

        internal void RetireDemand()
        {
            BoundTarget[] targets;
            lock (_bindingGate)
            {
                if (_demandRetired) return;
                _demandRetired = true;
                targets = _boundTargets;
                Volatile.Write(ref _boundTargets, []);
                Volatile.Write(ref _contractRouteView, ContractRouteView.Empty);
                _bindingSignal.TrySetResult(CapturePublicationFailureLocked());
            }
            var failures = new ContractRouteFailure(targets.Length * 2 + 1);
            try { _demandRetirement.Cancel(); }
            catch (Exception error) { failures.Add("demand-cancel", error); }
            foreach (var target in targets)
            {
                try { target.Connection.Binding.Publication.Invalidate(target.Token); }
                catch (Exception error) { failures.Add("demand-invalidate", error); }
                try { _ = _owner.RetireBindingCandidateAsync(target); }
                catch (Exception error) { failures.Add("demand-retirement-start", error); }
                // Session retains/observes each actual coalesced cleanup Task; no substitute task.
            }
            failures.ThrowIfAny();
        }

        internal void EnterBindingWaiter()
        {
            if (Interlocked.Increment(ref _bindingWaiters) > 64)
            {
                Interlocked.Decrement(ref _bindingWaiters);
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                    SharpLinkErrorDetails.ResourceExhausted.BindingWaiters, "Contract site binding waiter capacity is full.");
            }
        }
        internal void ExitBindingWaiter() => Interlocked.Decrement(ref _bindingWaiters);
        internal Task<Exception?> CaptureBindingSignal() { lock (_bindingGate) return _bindingSignal.Task; }
        internal void PulseBindingWaiters() { lock (_bindingGate) PulseBindingWaitersLocked(); }
        private void PulseBindingWaitersLocked()
        {
            var signal = _bindingSignal;
            _bindingSignal = NewBindingSignal();
            signal.TrySetResult(CapturePublicationFailureLocked());
        }
        private static TaskCompletionSource<Exception?> NewBindingSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void ValidateInvocationIdentity(RpcMethodDescriptor method, RpcMethodKind kind)
        {
            LocalControl.ThrowIfRetired();
            if (method.ContractId != ContractId)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                    SharpLinkErrorDetails.FailedPrecondition.MethodContractMismatch,
                    "The descriptor belongs to a different local contract.");
            if (method.Kind != kind || method.HasClientStreams != (method.ClientStreamCount != 0) ||
                method.ClientStreamCount is < 0 or > 127 ||
                kind is RpcMethodKind.Unary or RpcMethodKind.ServerStreaming && method.HasClientStreams ||
                kind == RpcMethodKind.OneWay && method.HasResponsePayload)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                    SharpLinkErrorDetails.FailedPrecondition.MethodShapeMismatch, "The invocation descriptor shape is inconsistent.");
        }

        private ClientConnection ReserveBoundConnection(RpcMethodDescriptor method,
            EndpointRetrySelectionState? retrySelection, AttemptOutcomeState? outcome, out BoundTarget bindingId)
        {
            LocalControl.ThrowIfRetired();
            if (Volatile.Read(ref _contractRouteRevisionExhausted)) throw RouteRevisionExhausted();
            if (_owner._cluster is not null)
                return ReserveClusterBoundConnection(retrySelection, outcome, out bindingId);
            var targets = Volatile.Read(ref _boundTargets);
            if (targets.Length == 0) throw NoBoundRoute();
            return ReserveFixedBoundConnection(targets, outcome, out bindingId);
        }

        private ClientConnection ReserveFixedBoundConnection(BoundTarget[] targets,
            AttemptOutcomeState? outcome, out BoundTarget bindingId)
        {
            var eligibleCount = 0;
            foreach (var target in targets)
                if (target.IsEligible) eligibleCount++;
            if (eligibleCount == 0)
                throw NoBoundRoute();
            if (outcome is not null && _owner._fixedEndpoint is { } endpoint &&
                !outcome.TryAcquire(new SharpLinkEndpointCandidate(endpoint, eligibleCount,
                    _owner.ActiveClientCallCount, generation: 0)))
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                    "The configured endpoint admission policy rejected the endpoint.");
            var selectedIndex = 0;
            if (targets.Length > 1)
            {
                var first = Random.Shared.Next(targets.Length);
                var second = Random.Shared.Next(targets.Length - 1);
                if (second >= first) second++;
                selectedIndex = targets[first].Connection.ActiveCallCount <= targets[second].Connection.ActiveCallCount
                    ? first : second;
            }
            for (var attempt = 0; attempt < targets.Length; attempt++)
            {
                var target = targets[(selectedIndex + attempt) % targets.Length];
                if (!target.IsEligible || LocalControl.IsRetired ||
                    !target.Connection.TryReserveExactCallAdmission())
                    continue;
                // Reservation and token were captured from the same immutable target. No redirect read.
                if (!target.IsValidForReservedCall || LocalControl.IsRetired)
                {
                    target.Connection.ReleaseCallAdmissionReservation();
                    continue;
                }
                bindingId = target;
                if (target.Connection.ActiveCallCount != 0)
                    _owner.EnsureExpansion();
                return target.Connection;
            }
            bindingId = null!;
            throw NoBoundRoute();
        }

        private static bool IsRetryableBindingCapacityFailure(Exception exception)
            => exception is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted } failure &&
               failure.DetailCode is SharpLinkErrorDetails.ResourceExhausted.BindingSlots or
                   SharpLinkErrorDetails.ResourceExhausted.BindingOperations or
                   SharpLinkErrorDetails.ResourceExhausted.BindingControlQueue;

        private sealed class BindingAttempt
        {
            internal bool Completed;
            internal Exception? Failure;
            internal Exception? PublicationFailure;
            internal bool RouteBuildFailed;
        }
        internal sealed class BoundTarget(ClientConnection connection, ulong token)
            : RpcBindingPublication(connection.Session, token)
        {
            internal ClientConnection Connection { get; } = connection;
            internal bool IsEligible => Connection.CanAcceptCalls && Connection.Binding.Publication.IsEligible(Token);
            internal bool IsValidForReservedCall => Connection.CanHonorReservedCallAdmission &&
                Connection.Binding.Publication.IsEligible(Token);
        }
    }
}
