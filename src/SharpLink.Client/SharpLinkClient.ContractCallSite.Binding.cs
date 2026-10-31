namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal sealed partial class ContractCallSite
    {
        private readonly Lock _bindingGate = new();
        private readonly Dictionary<ClientConnection, BindingAttempt> _bindingAttempts = [];
        private BoundTarget[] _boundTargets = [];
        private TaskCompletionSource _bindingSignal = NewBindingSignal();
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

        internal bool TryCompleteReadyPreparation(TimeSpan timeout, long started, CancellationToken cancellationToken)
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
                return HasBoundRoute;
            }
        }

        internal bool TryBeginBinding(ClientConnection connection, bool retryCompletedFailures)
        {
            lock (_bindingGate)
            {
                if (_demandRetired || LocalControl.IsRetired)
                    return false;
                foreach (var stale in _bindingAttempts.Keys.Where(static item => !item.Session.IsConnected).ToArray())
                    _bindingAttempts.Remove(stale);
                if (_bindingAttempts.TryGetValue(connection, out var attempt))
                {
                    if (!attempt.Completed || attempt.Failure is not null && !retryCompletedFailures &&
                        !IsRetryableBindingCapacityFailure(attempt.Failure))
                        return false; // deterministic failures wait for a cold Session/Manifest/invalidation event
                    if (_boundTargets.Any(target => ReferenceEquals(target.Connection, connection) && target.IsEligible))
                        return false;
                }
                _bindingAttempts[connection] = new BindingAttempt();
                return true;
            }
        }

        internal void PublishBinding(ClientConnection connection, ulong token)
        {
            lock (_bindingGate)
            {
                if (_demandRetired || LocalControl.IsRetired || !connection.CanAcceptCalls)
                    throw NoBoundRoute();
                var targets = _boundTargets.Where(target => !ReferenceEquals(target.Connection, connection) && target.IsEligible)
                    .Append(new BoundTarget(connection, token)).ToArray();
                Volatile.Write(ref _boundTargets, targets);
                _bindingAttempts[connection].Completed = true;
                PulseBindingWaitersLocked();
            }
        }

        internal void BindingFailed(ClientConnection connection, Exception exception)
        {
            lock (_bindingGate)
            {
                if (_bindingAttempts.TryGetValue(connection, out var attempt))
                {
                    attempt.Completed = true;
                    attempt.Failure = exception;
                }
                PulseBindingWaitersLocked();
            }
        }

        internal Exception? CaptureBindingFailure()
        {
            lock (_bindingGate)
            {
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
                PulseBindingWaitersLocked();
            }
            _demandRetirement.Cancel();
            foreach (var target in targets)
            {
                target.Connection.Binding.Publication.Invalidate(target.Token);
                _owner.TrackFrameworkTask(ReleaseRetiredBindingAsync(target), "ReleaseContractBinding");
            }
        }

        private static async Task ReleaseRetiredBindingAsync(BoundTarget target)
        {
            try
            {
                if (target.Connection.Session.IsConnected)
                    await target.Connection.Binding.ReleaseAsync(target.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                target.Connection.Session.NotifyDisconnected(exception);
            }
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
        internal Task CaptureBindingSignal() { lock (_bindingGate) return _bindingSignal.Task; }
        internal void PulseBindingWaiters() { lock (_bindingGate) PulseBindingWaitersLocked(); }
        private void PulseBindingWaitersLocked()
        {
            var signal = _bindingSignal;
            _bindingSignal = NewBindingSignal();
            signal.TrySetResult();
        }
        private static TaskCompletionSource NewBindingSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            EndpointRetrySelectionState? retrySelection, AttemptOutcomeState? outcome, out ulong bindingId)
        {
            LocalControl.ThrowIfRetired();
            var targets = Volatile.Read(ref _boundTargets);
            if (targets.Length == 0)
                throw NoBoundRoute();
            if (_owner._cluster is null)
                return ReserveFixedBoundConnection(targets, outcome, out bindingId);

            // Cluster-specific cold scoped views remain an explicit nonshipping integration gate.
            // Never carry a token across a replacement redirect: only this exact returned physical owner qualifies.
            var connection = _owner.GetReadyConnection(method, retrySelection, outcome);
            foreach (var target in targets)
            {
                if (ReferenceEquals(target.Connection, connection) && target.IsEligible && !LocalControl.IsRetired)
                {
                    bindingId = target.Token;
                    return connection;
                }
            }
            connection.ReleaseCallAdmissionReservation();
            bindingId = 0;
            throw NoBoundRoute();
        }

        private ClientConnection ReserveFixedBoundConnection(BoundTarget[] targets,
            AttemptOutcomeState? outcome, out ulong bindingId)
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
                if (!target.IsEligible || LocalControl.IsRetired)
                {
                    target.Connection.ReleaseCallAdmissionReservation();
                    continue;
                }
                bindingId = target.Token;
                if (target.Connection.ActiveCallCount != 0)
                    _owner.EnsureExpansion();
                return target.Connection;
            }
            bindingId = 0;
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
        }
        private sealed record BoundTarget(ClientConnection Connection, ulong Token)
        {
            internal bool IsEligible => Connection.CanAcceptCalls && Connection.Binding.Publication.IsEligible(Token);
        }
    }
}
