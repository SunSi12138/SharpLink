using System.Threading.RateLimiting;

namespace SharpLink.Server;

internal sealed partial class AdmissionRateState
{
    private sealed class Counter : IAdmissionRateWaiterOwner
    {
        private static long s_nextIdentity;

        private readonly Lock _gate = new();
        private readonly TimeProvider _timeProvider;
        private AdmissionRateWaitQueue _waiters;
        private ITimer? _timer;
        private long _windowStart;
        private long _activeWindowTimestampTicks;
        private long _consumed;
        private long _nextSequence = 1;
        private long _retiredThroughSequence;
        private long _pendingSequence;
        private long _pendingBoundary;
        private long _pendingWindowTimestampTicks;
        private int _activeLimit;
        private int _queuedLimit;
        private int _pendingLimit;
        private int _references = 1;
        private int _disposed;

        internal Counter(int permitLimit, TimeSpan window, TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
            _activeLimit = permitLimit;
            _queuedLimit = permitLimit;
            _activeWindowTimestampTicks = ToTimestampTicks(window.Ticks);
            _windowStart = _timeProvider.GetTimestamp();
            Identity = Interlocked.Increment(ref s_nextIdentity);
        }

        internal long Identity { get; }

        internal int WaitingCount
        {
            get { lock (_gate) return _waiters.Count; }
        }

        internal long Consumed
        {
            get
            {
                lock (_gate)
                {
                    AdvanceLocked(_timeProvider.GetTimestamp());
                    return _consumed;
                }
            }
        }

        internal int ActiveLimit
        {
            get
            {
                lock (_gate)
                {
                    AdvanceLocked(_timeProvider.GetTimestamp());
                    return _activeLimit;
                }
            }
        }

        internal int QueuedLimit
        {
            get
            {
                lock (_gate)
                {
                    AdvanceLocked(_timeProvider.GetTimestamp());
                    return _queuedLimit;
                }
            }
        }

        internal TimeSpan ActiveWindow
        {
            get
            {
                lock (_gate)
                {
                    AdvanceLocked(_timeProvider.GetTimestamp());
                    return TimestampDeltaToTimeSpan(_activeWindowTimestampTicks);
                }
            }
        }

        internal bool HasPendingTarget
        {
            get { lock (_gate) return _pendingSequence != 0; }
        }

        internal DynamicFixedWindowActivationMode ResolveActivation(
            long requestedWindowTimestampTicks,
            DynamicFixedWindowActivationMode? requestedActivation)
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                AdvanceLocked(_timeProvider.GetTimestamp());

                var requestedWindowIsActive =
                    requestedWindowTimestampTicks == _activeWindowTimestampTicks;
                var hasPendingTarget = _pendingSequence != 0;
                if (requestedActivation == DynamicFixedWindowActivationMode.Immediate)
                {
                    if (!requestedWindowIsActive || hasPendingTarget)
                    {
                        throw new InvalidOperationException(
                            "Immediate FixedWindow updates require the requested Window to be the active Window with no pending Window activation.");
                    }
                    return DynamicFixedWindowActivationMode.Immediate;
                }

                if (requestedActivation == DynamicFixedWindowActivationMode.NextWindowBoundary)
                    return DynamicFixedWindowActivationMode.NextWindowBoundary;

                return requestedWindowIsActive && !hasPendingTarget
                    ? DynamicFixedWindowActivationMode.Immediate
                    : DynamicFixedWindowActivationMode.NextWindowBoundary;
            }
        }

        internal AdmissionRateState CreateSuccessor(
            AdmissionRateStateDefinition definition,
            long windowTimestampTicks,
            DynamicFixedWindowActivationMode activationMode)
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var sequence = checked(++_nextSequence);
                _references = checked(_references + 1);
                return new AdmissionRateState(
                    definition,
                    this,
                    sequence,
                    windowTimestampTicks,
                    activationMode);
            }
        }

        internal void CommitTransition(
            AdmissionRateState source,
            AdmissionRateState target)
        {
            if (!ReferenceEquals(source._fixedCounter, this) || !ReferenceEquals(target._fixedCounter, this))
                throw new InvalidOperationException("Dynamic FixedWindow transition crossed logical counters.");

            AdmissionRateWaiter? granted;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var now = _timeProvider.GetTimestamp();
                granted = PublishLocked(source, now);
                AdvanceLocked(now);
                var sourceLimit = GetDirectLimitLocked(source);
                var boundary = SaturatingAdd(_windowStart, _activeWindowTimestampTicks);
                target.FinalizeFixedForCommit(sourceLimit, boundary);
            }
            AdmissionRateWaitQueue.CompleteGranted(granted);
        }

        internal void Publish(AdmissionRateState policy)
        {
            if (Volatile.Read(ref policy._fixedPublished) != 0)
                return;

            AdmissionRateWaiter? granted;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                granted = PublishLocked(policy, _timeProvider.GetTimestamp());
            }
            AdmissionRateWaitQueue.CompleteGranted(granted);
        }

        internal RateLimitLease AttemptAcquire(AdmissionRateState policy)
        {
            lock (_gate)
            {
                if (_disposed != 0)
                    return AdmissionRateLeases.Failed;

                var now = _timeProvider.GetTimestamp();
                AdvanceLocked(now);
                if (_waiters.Count != 0 || _consumed >= GetDirectLimitLocked(policy))
                    return AdmissionRateLeases.Failed;

                _consumed++;
                return AdmissionRateLeases.Acquired;
            }
        }

        internal ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<RateLimitLease>(cancellationToken);

            AdmissionRateWaiter waiter;
            lock (_gate)
            {
                if (_disposed != 0)
                    return ValueTask.FromResult<RateLimitLease>(AdmissionRateLeases.Failed);

                var now = _timeProvider.GetTimestamp();
                AdvanceLocked(now);
                if (_waiters.Count == 0 && _consumed < _queuedLimit)
                {
                    _consumed++;
                    return ValueTask.FromResult<RateLimitLease>(AdmissionRateLeases.Acquired);
                }

                waiter = new AdmissionRateWaiter(this, cancellationToken);
                _waiters.Enqueue(waiter);
                ScheduleTimerLocked(now);
            }

            waiter.RegisterCancellation();
            return new ValueTask<RateLimitLease>(waiter.Task);
        }

        internal void ReleaseView()
        {
            AdmissionRateWaiter? failed = null;
            ITimer? timer = null;
            lock (_gate)
            {
                if (--_references < 0)
                    throw new InvalidOperationException("Dynamic FixedWindow view reference count underflowed.");
                if (_references != 0 || _disposed != 0)
                    return;

                _disposed = 1;
                failed = _waiters.DetachAll();
                timer = _timer;
                _timer = null;
            }
            timer?.Dispose();
            AdmissionRateWaitQueue.CompleteFailed(failed);
        }

        internal long ToTimestampTicks(long timeSpanTicks)
        {
            var scaled = (decimal)timeSpanTicks * _timeProvider.TimestampFrequency /
                         TimeSpan.TicksPerSecond;
            if (scaled >= long.MaxValue)
                return long.MaxValue;
            return Math.Max(1, (long)Math.Ceiling(scaled));
        }

        internal TimeSpan TimestampDeltaToTimeSpan(long timestampTicks)
        {
            var scaled = (decimal)timestampTicks * TimeSpan.TicksPerSecond /
                         _timeProvider.TimestampFrequency;
            if (scaled >= TimeSpan.MaxValue.Ticks)
                return TimeSpan.MaxValue;
            return TimeSpan.FromTicks(Math.Max(1, (long)Math.Ceiling(scaled)));
        }

        private AdmissionRateWaiter? PublishLocked(AdmissionRateState policy, long now)
        {
            if (Volatile.Read(ref policy._fixedPublished) != 0)
                return null;
            if (Volatile.Read(ref policy._fixedCommitted) == 0)
                throw new InvalidOperationException("Uncommitted Dynamic FixedWindow policy became visible.");

            AdvanceLocked(now);
            if (policy._fixedActivationMode == DynamicFixedWindowActivationMode.Immediate)
            {
                if (policy._fixedWindowTimestampTicks == _activeWindowTimestampTicks)
                {
                    ClearPendingLocked();
                }
                else if (now < policy._fixedActivationBoundary)
                {
                    StagePendingLocked(policy);
                }
                else
                {
                    ActivateLatePublishedPolicyLocked(policy, now);
                    policy.MarkFixedPublishedLocked();
                    return GrantWaitersLocked(now);
                }

                _queuedLimit = policy._definition.Limit;
            }
            else if (now < policy._fixedActivationBoundary)
            {
                StagePendingLocked(policy);
                _queuedLimit = policy._fixedPreActivationLimit;
            }
            else
            {
                ActivateLatePublishedPolicyLocked(policy, now);
            }

            policy.MarkFixedPublishedLocked();
            return GrantWaitersLocked(now);
        }

        private void StagePendingLocked(AdmissionRateState policy)
        {
            _pendingSequence = policy._fixedSequence;
            _pendingBoundary = policy._fixedActivationBoundary;
            _pendingLimit = policy._definition.Limit;
            _pendingWindowTimestampTicks = policy._fixedWindowTimestampTicks;
        }

        private int GetDirectLimitLocked(AdmissionRateState policy)
        {
            if (!ReferenceEquals(policy._fixedCounter, this))
                throw new InvalidOperationException("Dynamic FixedWindow policy belongs to another counter.");
            if (policy._fixedSequence <= _retiredThroughSequence)
                return _activeLimit;
            if (policy._fixedActivationMode == DynamicFixedWindowActivationMode.Immediate)
                return policy._definition.Limit;
            if (Volatile.Read(ref policy._fixedCommitted) == 0)
                throw new InvalidOperationException("Uncommitted Dynamic FixedWindow policy became visible.");
            return policy._fixedPreActivationLimit;
        }

        private void AdvanceLocked(long now)
        {
            if (_pendingSequence != 0 && now >= _pendingBoundary)
            {
                _windowStart = _pendingBoundary;
                _consumed = 0;
                _activeLimit = _pendingLimit;
                _queuedLimit = _pendingLimit;
                _activeWindowTimestampTicks = _pendingWindowTimestampTicks;
                _retiredThroughSequence = Math.Max(_retiredThroughSequence, _pendingSequence);
                ClearPendingLocked();
            }

            var boundary = SaturatingAdd(_windowStart, _activeWindowTimestampTicks);
            if (now < boundary)
                return;

            var elapsed = now - _windowStart;
            var windows = elapsed / _activeWindowTimestampTicks;
            _windowStart = SaturatingAdd(
                _windowStart,
                SaturatingMultiply(windows, _activeWindowTimestampTicks));
            _consumed = 0;
        }

        private void ActivateLatePublishedPolicyLocked(
            AdmissionRateState policy,
            long now)
        {
            var activationBoundary = policy._fixedActivationBoundary;
            _activeLimit = policy._definition.Limit;
            _queuedLimit = policy._definition.Limit;
            _activeWindowTimestampTicks = policy._fixedWindowTimestampTicks;
            _retiredThroughSequence = Math.Max(_retiredThroughSequence, policy._fixedSequence);
            ClearPendingLocked();

            if (_windowStart < activationBoundary)
                _windowStart = activationBoundary;
            if (_windowStart > activationBoundary ||
                now >= SaturatingAdd(_windowStart, _activeWindowTimestampTicks))
            {
                _windowStart = now;
            }
        }

        private AdmissionRateWaiter? GrantWaitersLocked(long now)
        {
            AdvanceLocked(now);
            AdmissionRateWaiter? grantedHead = null;
            AdmissionRateWaiter? grantedTail = null;
            while (!_waiters.IsEmpty && _consumed < _queuedLimit)
            {
                var waiter = _waiters.Dequeue();
                _consumed++;
                if (grantedTail is null)
                    grantedHead = waiter;
                else
                    grantedTail.Next = waiter;
                grantedTail = waiter;
            }
            ScheduleTimerLocked(now);
            return grantedHead;
        }

        private void ScheduleTimerLocked(long now)
        {
            if (_disposed != 0 || _waiters.IsEmpty)
            {
                _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            AdvanceLocked(now);
            if (_consumed < _queuedLimit)
                return;

            var next = _pendingSequence != 0
                ? _pendingBoundary
                : SaturatingAdd(_windowStart, _activeWindowTimestampTicks);
            if (next == long.MaxValue)
                return;
            var due = TimestampDeltaToTimeSpan(Math.Max(1, next - now));
            _timer ??= _timeProvider.CreateTimer(
                static state => ((Counter)state!).OnTimer(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }

        private void OnTimer()
        {
            AdmissionRateWaiter? granted;
            lock (_gate)
            {
                if (_disposed != 0)
                    return;
                granted = GrantWaitersLocked(_timeProvider.GetTimestamp());
            }
            AdmissionRateWaitQueue.CompleteGranted(granted);
        }

        void IAdmissionRateWaiterOwner.CancelRateWaiter(AdmissionRateWaiter waiter) => CancelWaiter(waiter);

        private void CancelWaiter(AdmissionRateWaiter waiter)
        {
            var removed = false;
            lock (_gate)
            {
                removed = _waiters.Remove(waiter);
                if (removed)
                    ScheduleTimerLocked(_timeProvider.GetTimestamp());
            }
            if (removed)
                waiter.CompleteCanceled();
        }

        private void ClearPendingLocked()
        {
            _pendingSequence = 0;
            _pendingBoundary = 0;
            _pendingLimit = 0;
            _pendingWindowTimestampTicks = 0;
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(AdmissionRateState));
        }

        private static long SaturatingAdd(long left, long right)
        {
            if (right <= 0)
                return left;
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }

        private static long SaturatingMultiply(long left, long right)
        {
            if (left <= 0 || right <= 0)
                return 0;
            return left > long.MaxValue / right ? long.MaxValue : left * right;
        }
    }

}
