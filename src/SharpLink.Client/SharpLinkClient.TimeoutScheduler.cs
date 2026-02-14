namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private interface IPooledTimeoutState
    {
        void ReturnOnDispose();
    }

    private sealed class RequestTimeoutScheduler : IDisposable
    {
        private const int StripeCount = 8;
        private readonly Stripe[] _stripes = new Stripe[StripeCount];
        private long _nextId;
        private int _disposed;

        public RequestTimeoutScheduler()
        {
            for (var i = 0; i < StripeCount; i++)
                _stripes[i] = new Stripe();
        }

        public TimeoutRegistration Schedule(TimeSpan timeout, Action<object?> callback, IPooledTimeoutState state)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            ArgumentNullException.ThrowIfNull(callback);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var dueTicks = DateTime.UtcNow.Add(timeout).Ticks;
            var id = Interlocked.Increment(ref _nextId);
            var stripe = GetStripe(id);
            _stripes[stripe].Schedule(id, dueTicks, callback, state);
            return new TimeoutRegistration(this, stripe, id, state);
        }

        public void Cancel(int stripe, long id)
        {
            if ((uint)stripe >= StripeCount)
                return;

            _stripes[stripe].Cancel(id);
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
            private readonly PriorityQueue<ScheduledTimeout, long> _queue = new();
            private readonly HashSet<long> _canceled = [];
            private readonly Timer _timer;
            private bool _disposed;

            public Stripe()
            {
                _timer = new Timer(static s => ((Stripe)s!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            public void Schedule(long id, long dueTicks, Action<object?> callback, object? state)
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;

                    _queue.Enqueue(new ScheduledTimeout(id, dueTicks, callback, state), dueTicks);
                    UpdateTimerUnsafe();
                }
            }

            public void Cancel(long id)
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;

                    _canceled.Add(id);
                    if (_canceled.Count > 2048)
                        CompactCanceledUnsafe();
                }
            }

            private void OnTimer()
            {
                while (true)
                {
                    ScheduledTimeout next;
                    lock (_gate)
                    {
                        if (_disposed || _queue.Count == 0)
                            return;

                        var nowTicks = DateTime.UtcNow.Ticks;
                        next = _queue.Peek();
                        if (next.DueTicks > nowTicks)
                        {
                            UpdateTimerUnsafe();
                            return;
                        }

                        _queue.Dequeue();
                        if (_canceled.Remove(next.Id))
                            continue;
                    }

                    next.Callback(next.State);
                }
            }

            private void UpdateTimerUnsafe()
            {
                if (_queue.Count == 0)
                {
                    _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    return;
                }

                var nowTicks = DateTime.UtcNow.Ticks;
                var dueTicks = _queue.Peek().DueTicks;
                var delayTicks = dueTicks - nowTicks;
                var delay = delayTicks <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(delayTicks);
                _timer.Change(delay, Timeout.InfiniteTimeSpan);
            }

            private void CompactCanceledUnsafe()
            {
                if (_queue.Count == 0)
                {
                    _canceled.Clear();
                    return;
                }

                var keep = new HashSet<long>();
                foreach (var item in _queue.UnorderedItems)
                    keep.Add(item.Element.Id);

                _canceled.RemoveWhere(id => !keep.Contains(id));
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;

                    _disposed = true;
                    _queue.Clear();
                    _canceled.Clear();
                }

                _timer.Dispose();
            }
        }

        private readonly record struct ScheduledTimeout(long Id, long DueTicks, Action<object?> Callback, object? State);
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
            scheduler?.Cancel(stripe, id);
            state?.ReturnOnDispose();
        }
    }
}
