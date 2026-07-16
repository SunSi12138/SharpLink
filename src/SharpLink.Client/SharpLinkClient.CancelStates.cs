namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private interface IPooledCancelState
    {
        void ReturnOnDispose();
    }

    private struct PooledCancellationRegistration(CancellationTokenRegistration registration, IPooledCancelState? state) : IDisposable, IAsyncDisposable
    {
        private IPooledCancelState? _state = state;

        public void Dispose()
        {
            registration.Dispose();
            _state?.ReturnOnDispose();
            _state = null;
        }

        public async ValueTask DisposeAsync()
        {
            await registration.DisposeAsync();
            _state?.ReturnOnDispose();
            _state = null;
        }
    }

    private sealed class RequestCancelState : IPooledCancelState
    {
        private static readonly ConcurrentQueue<RequestCancelState> SPool = new();

        private SharpLinkClient? _client;
        private int _lifecycle;

        public SharpLinkClient Client => _client!;
        public long RequestId { get; private set; }
        public bool IsOneWay { get; private set; }
        public CancellationToken UserToken { get; private set; }

        private RequestCancelState() { }

        public static RequestCancelState Rent(SharpLinkClient client, long requestId, bool isOneWay, CancellationToken userToken)
        {
            if (!SPool.TryDequeue(out var state))
                state = new RequestCancelState();

            state._client = client;
            state.RequestId = requestId;
            state.IsOneWay = isOneWay;
            state.UserToken = userToken;
            state._lifecycle = 0;
            return state;
        }

        public bool TryBeginInvocation() => Interlocked.Exchange(ref _lifecycle, 1) == 0;

        public void ReturnOnDispose()
        {
            if (Interlocked.Exchange(ref _lifecycle, 1) != 0)
                return;

            ReturnCore();
        }

        public void ReturnAfterInvocation() => ReturnCore();

        private void ReturnCore()
        {
            _client = null;
            RequestId = 0;
            IsOneWay = false;
            UserToken = CancellationToken.None;
            SPool.Enqueue(this);
        }
    }

    private sealed class StreamCancelState : IPooledCancelState
    {
        private static readonly ConcurrentQueue<StreamCancelState> SPool = new();

        private SharpLinkClient? _client;
        private int _lifecycle;

        public SharpLinkClient Client => _client!;
        public long RequestId { get; private set; }
        public CancellationToken UserToken { get; private set; }

        private StreamCancelState() { }

        public static StreamCancelState Rent(SharpLinkClient client, long requestId, CancellationToken userToken)
        {
            if (!SPool.TryDequeue(out var state))
                state = new StreamCancelState();

            state._client = client;
            state.RequestId = requestId;
            state.UserToken = userToken;
            state._lifecycle = 0;
            return state;
        }

        public bool TryBeginInvocation() => Interlocked.Exchange(ref _lifecycle, 1) == 0;

        public void ReturnOnDispose()
        {
            if (Interlocked.Exchange(ref _lifecycle, 1) != 0)
                return;

            ReturnCore();
        }

        public void ReturnAfterInvocation() => ReturnCore();

        private void ReturnCore()
        {
            _client = null;
            RequestId = 0;
            UserToken = CancellationToken.None;
            SPool.Enqueue(this);
        }
    }

    private sealed class RequestTimeoutState : IPooledTimeoutState
    {
        [ThreadStatic]
        private static RequestTimeoutState? t_cached;
        private static readonly ConcurrentStack<RequestTimeoutState> SPool = new();

        private SharpLinkClient? _client;
        private int _lifecycle;

        public SharpLinkClient Client => _client!;
        public long RequestId { get; private set; }
        public bool IsOneWay { get; private set; }
        public int TimeoutHeapIndex { get; set; } = -1;

        private RequestTimeoutState() { }

        public static RequestTimeoutState Rent(SharpLinkClient client, long requestId, bool isOneWay)
        {
            var state = t_cached;
            if (state is not null)
                t_cached = null;
            else if (!SPool.TryPop(out state))
                state = new RequestTimeoutState();

            state._client = client;
            state.RequestId = requestId;
            state.IsOneWay = isOneWay;
            state.TimeoutHeapIndex = -1;
            state._lifecycle = 0;
            return state;
        }

        public bool TryBeginInvocation() => Interlocked.Exchange(ref _lifecycle, 1) == 0;

        public void ReturnOnDispose()
        {
            if (Interlocked.Exchange(ref _lifecycle, 1) != 0)
                return;

            ReturnCore();
        }

        public void ReturnAfterInvocation() => ReturnCore();

        public void ReturnAfterCancellation() => ReturnCore();

        private void ReturnCore()
        {
            _client = null;
            RequestId = 0;
            IsOneWay = false;
            TimeoutHeapIndex = -1;
            if (t_cached is null)
                t_cached = this;
            else
                SPool.Push(this);
        }
    }

    private sealed class StreamTimeoutState : IPooledTimeoutState
    {
        [ThreadStatic]
        private static StreamTimeoutState? t_cached;
        private static readonly ConcurrentStack<StreamTimeoutState> SPool = new();

        private SharpLinkClient? _client;
        private int _lifecycle;

        public SharpLinkClient Client => _client!;
        public long RequestId { get; private set; }
        public int TimeoutHeapIndex { get; set; } = -1;

        private StreamTimeoutState() { }

        public static StreamTimeoutState Rent(SharpLinkClient client, long requestId)
        {
            var state = t_cached;
            if (state is not null)
                t_cached = null;
            else if (!SPool.TryPop(out state))
                state = new StreamTimeoutState();

            state._client = client;
            state.RequestId = requestId;
            state.TimeoutHeapIndex = -1;
            state._lifecycle = 0;
            return state;
        }

        public bool TryBeginInvocation() => Interlocked.Exchange(ref _lifecycle, 1) == 0;

        public void ReturnOnDispose()
        {
            if (Interlocked.Exchange(ref _lifecycle, 1) != 0)
                return;

            ReturnCore();
        }

        public void ReturnAfterInvocation() => ReturnCore();

        public void ReturnAfterCancellation() => ReturnCore();

        private void ReturnCore()
        {
            _client = null;
            RequestId = 0;
            TimeoutHeapIndex = -1;
            if (t_cached is null)
                t_cached = this;
            else
                SPool.Push(this);
        }
    }
}
