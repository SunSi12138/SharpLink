namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private interface IPooledCancelState
    {
        void ReturnOnDispose();
    }

    private struct PooledCancellationRegistration(CancellationTokenRegistration registration, IPooledCancelState? state) : IDisposable, IAsyncDisposable
    {
        private CancellationTokenRegistration _registration = registration;
        private IPooledCancelState? _state = state;

        public void Dispose()
        {
            _registration.Dispose();
            _state?.ReturnOnDispose();
            _state = null;
        }

        public async ValueTask DisposeAsync()
        {
            await _registration.DisposeAsync();
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
            UserToken = default;
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
            UserToken = default;
            SPool.Enqueue(this);
        }
    }

    private sealed class RequestTimeoutState : IPooledTimeoutState
    {
        private static readonly ConcurrentQueue<RequestTimeoutState> SPool = new();

        private SharpLinkClient? _client;
        private int _lifecycle;

        public SharpLinkClient Client => _client!;
        public long RequestId { get; private set; }
        public bool IsOneWay { get; private set; }

        private RequestTimeoutState() { }

        public static RequestTimeoutState Rent(SharpLinkClient client, long requestId, bool isOneWay)
        {
            if (!SPool.TryDequeue(out var state))
                state = new RequestTimeoutState();

            state._client = client;
            state.RequestId = requestId;
            state.IsOneWay = isOneWay;
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
            SPool.Enqueue(this);
        }
    }

    private sealed class StreamTimeoutState : IPooledTimeoutState
    {
        private static readonly ConcurrentQueue<StreamTimeoutState> SPool = new();

        private SharpLinkClient? _client;
        private int _lifecycle;

        public SharpLinkClient Client => _client!;
        public long RequestId { get; private set; }

        private StreamTimeoutState() { }

        public static StreamTimeoutState Rent(SharpLinkClient client, long requestId)
        {
            if (!SPool.TryDequeue(out var state))
                state = new StreamTimeoutState();

            state._client = client;
            state.RequestId = requestId;
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
            SPool.Enqueue(this);
        }
    }
}
