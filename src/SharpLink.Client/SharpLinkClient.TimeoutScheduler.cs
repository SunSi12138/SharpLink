namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private interface IPooledTimeoutState
    {
        int TimeoutHeapIndex { get; set; }
        void ReturnOnDispose();
        void ReturnAfterCancellation();
    }

    private sealed class RequestTimeoutScheduler : IDisposable
    {
        private const int StripeCount = 32;
        private readonly Stripe[] _stripes = new Stripe[StripeCount];
        private int _disposed;

        public RequestTimeoutScheduler()
        {
            for (var i = 0; i < StripeCount; i++)
                _stripes[i] = new Stripe();
        }

        public TimeoutRegistration Schedule(
            long requestId,
            DateTimeOffset deadline,
            Action<object?> callback,
            IPooledTimeoutState state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var dueTicks = deadline.UtcDateTime.Ticks;
            var stripe = GetStripe(requestId);
            _stripes[stripe].Schedule(requestId, dueTicks, callback, state);
            return new TimeoutRegistration(this, stripe, requestId, state);
        }

        public bool Cancel(int stripe, long id, IPooledTimeoutState state)
        {
            if ((uint)stripe >= StripeCount)
                return false;

            return _stripes[stripe].Cancel(id, state);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            for (var i = 0; i < StripeCount; i++)
                _stripes[i].Dispose();
        }

        private static int GetStripe(long id)
        {
            var hash = unchecked((int)(id ^ (id >> 32)));
            hash &= int.MaxValue;
            return hash & (StripeCount - 1);
        }

        private sealed class Stripe : IDisposable
        {
            private readonly Lock _gate = new();
            private readonly List<ScheduledTimeout> _heap = [];
            private readonly Timer _timer;
            private long _armedDueTicks;
            private bool _disposed;

            public Stripe()
            {
                _timer = new Timer(static s => ((Stripe)s!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            public void Schedule(long id, long dueTicks, Action<object?> callback, IPooledTimeoutState state)
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;

                    var index = _heap.Count;
                    _heap.Add(new ScheduledTimeout(id, dueTicks, callback, state));
                    SiftUpUnsafe(index);
                    UpdateTimerUnsafe();
                }
            }

            public bool Cancel(long id, IPooledTimeoutState state)
            {
                lock (_gate)
                {
                    if (_disposed)
                        return false;

                    var index = state.TimeoutHeapIndex;
                    if ((uint)index >= (uint)_heap.Count || _heap[index].Id != id)
                        return false;
                    RemoveAtUnsafe(index);
                    return true;
                }
            }

            private void OnTimer()
            {
                while (true)
                {
                    ScheduledTimeout next;
                    lock (_gate)
                    {
                        _armedDueTicks = 0;
                        if (_disposed || _heap.Count == 0)
                            return;

                        var nowTicks = DateTime.UtcNow.Ticks;
                        next = _heap[0];
                        if (next.DueTicks > nowTicks)
                        {
                            UpdateTimerUnsafe();
                            return;
                        }

                        RemoveAtUnsafe(0);
                    }

                    next.Callback(next.State);
                }
            }

            private void UpdateTimerUnsafe()
            {
                if (_heap.Count == 0)
                    return;

                var nowTicks = DateTime.UtcNow.Ticks;
                var dueTicks = _heap[0].DueTicks;
                if (_armedDueTicks != 0 && _armedDueTicks <= dueTicks)
                    return;
                var delayTicks = dueTicks - nowTicks;
                var delay = delayTicks <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(delayTicks);
                _timer.Change(delay, Timeout.InfiniteTimeSpan);
                _armedDueTicks = dueTicks;
            }

            private void RemoveAtUnsafe(int index)
            {
                var lastIndex = _heap.Count - 1;
                var removed = _heap[index];
                removed.State.TimeoutHeapIndex = -1;
                if (index == lastIndex)
                {
                    _heap.RemoveAt(lastIndex);
                    return;
                }

                var replacement = _heap[lastIndex];
                _heap[index] = replacement;
                _heap.RemoveAt(lastIndex);
                replacement.State.TimeoutHeapIndex = index;

                var parent = (index - 1) >> 1;
                if (index > 0 && ComesBefore(replacement, _heap[parent]))
                    SiftUpUnsafe(index);
                else
                    SiftDownUnsafe(index);
            }

            private void SiftUpUnsafe(int index)
            {
                var item = _heap[index];
                while (index > 0)
                {
                    var parent = (index - 1) >> 1;
                    if (!ComesBefore(item, _heap[parent]))
                        break;

                    SetHeapItemUnsafe(index, _heap[parent]);
                    index = parent;
                }

                SetHeapItemUnsafe(index, item);
            }

            private void SiftDownUnsafe(int index)
            {
                var item = _heap[index];
                var count = _heap.Count;
                while (true)
                {
                    var left = (index << 1) + 1;
                    if (left >= count)
                        break;

                    var right = left + 1;
                    var child = right < count && ComesBefore(_heap[right], _heap[left])
                        ? right
                        : left;
                    if (!ComesBefore(_heap[child], item))
                        break;

                    SetHeapItemUnsafe(index, _heap[child]);
                    index = child;
                }

                SetHeapItemUnsafe(index, item);
            }

            private void SetHeapItemUnsafe(int index, ScheduledTimeout item)
            {
                _heap[index] = item;
                item.State.TimeoutHeapIndex = index;
            }

            private static bool ComesBefore(ScheduledTimeout left, ScheduledTimeout right)
                => left.DueTicks < right.DueTicks ||
                   (left.DueTicks == right.DueTicks && left.Id < right.Id);

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;

                    _disposed = true;
                    foreach (var item in _heap)
                        item.State.TimeoutHeapIndex = -1;
                    _heap.Clear();
                }

                _timer.Dispose();
            }
        }

        private readonly record struct ScheduledTimeout(
            long Id,
            long DueTicks,
            Action<object?> Callback,
            IPooledTimeoutState State);
    }

    private readonly struct TimeoutRegistration(
        RequestTimeoutScheduler? scheduler,
        int stripe,
        long id,
        IPooledTimeoutState? state = null)
        : IDisposable
    {

        public void Dispose()
        {
            if (state is null)
                return;
            if (scheduler?.Cancel(stripe, id, state) == true)
                state.ReturnAfterCancellation();
            else
                state.ReturnOnDispose();
        }
    }
}
