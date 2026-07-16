


namespace SharpLink.Client;

internal sealed partial class SharpLinkClient(IClientTransportFactory transportFactory) : IRpcChannel, ISharpLinkClient
{
    private const string DefaultHandshakeMessage = "";
    private readonly SharpLinkRuntimeContext _runtimeContext = new SharpLinkRuntimeContextBuilder().Build();
    private readonly StripedLongSet _serverStreamRequestIds = new();
    private readonly StripedLongSet _locallyCanceledRequestIds = new();
    private readonly PendingRequestTable _requestManager = new();
    private readonly ConcurrentDictionary<long, RpcSession> _requestSessions = [];
    private readonly ConcurrentDictionary<long, StreamCallLifetime> _streamCallLifetimes = [];
    private readonly RequestTimeoutScheduler _requestTimeoutScheduler = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Lock _stateGate = new();
    private readonly Lock _backgroundTasksGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private RpcSession? _session;
    private CancellationTokenSource? _sessionCts;
    private Task? _connectTask;
    private Task? _reconnectTask;
    private Task? _stopTask;
    private TaskCompletionSource<bool> _readySignal = CreateReadySignal();
    private int _state = (int)SharpLinkConnectionState.Created;
    private int _reconnectDelayMilliseconds = 100;
    private long _readyTimestamp;
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private readonly bool _hasRequestTimeout;
    private readonly TimeSpan _requestTimeoutValue;
    private readonly string _handshakeMessage = DefaultHandshakeMessage;
    private readonly SharpLinkProtocolOptions _protocolOptions = new();
    private readonly ILogger _logger = NullLogger<SharpLinkClient>.Instance;
    private readonly RpcSessionFlushOptions? _rpcSessionFlushOptions;

    public SharpLinkClient(
        IClientTransportFactory transportFactory,
        TimeSpan heartbeatInterval,
        TimeSpan heartbeatTimeout,
        TimeSpan? requestTimeout = null,
        string handshakeMessage = DefaultHandshakeMessage,
        SharpLinkProtocolOptions? protocolOptions = null,
        SharpLinkRuntimeContext? runtimeContext = null,
        RpcSessionFlushOptions? rpcSessionFlushOptions = null)
        : this(transportFactory)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatTimeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(handshakeMessage);
        if (heartbeatTimeout <= heartbeatInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than interval.");
        if (requestTimeout is { } timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            _hasRequestTimeout = true;
            _requestTimeoutValue = timeout;
        }

        _heartbeatInterval = heartbeatInterval;
        _heartbeatTimeout = heartbeatTimeout;
        _handshakeMessage = handshakeMessage;
        _runtimeContext = runtimeContext ?? new SharpLinkRuntimeContextBuilder().Build();
        _protocolOptions = (protocolOptions ?? _runtimeContext.Protocol).CloneValidated();
        _rpcSessionFlushOptions = rpcSessionFlushOptions;
        _serverStreamRequestIds = new StripedLongSet(_runtimeContext.Concurrency);
        _locallyCanceledRequestIds = new StripedLongSet(_runtimeContext.Concurrency);
        _requestManager = new PendingRequestTable(_protocolOptions.MaxPendingRequestsPerConnection, _runtimeContext.Codecs);
    }

    public SharpLinkClient(
        IClientTransportFactory transportFactory,
        TimeSpan heartbeatInterval,
        TimeSpan heartbeatTimeout,
        ILoggerFactory loggerFactory,
        TimeSpan? requestTimeout = null,
        string handshakeMessage = DefaultHandshakeMessage,
        SharpLinkProtocolOptions? protocolOptions = null,
        SharpLinkRuntimeContext? runtimeContext = null,
        RpcSessionFlushOptions? rpcSessionFlushOptions = null)
        : this(transportFactory, heartbeatInterval, heartbeatTimeout, requestTimeout, handshakeMessage, protocolOptions,
            runtimeContext, rpcSessionFlushOptions)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<SharpLinkClient>();
    }

    public IRpcRuntimeContext RuntimeContext => _runtimeContext;

    public SharpLinkConnectionState State
        => (SharpLinkConnectionState)Volatile.Read(ref _state);

    public ValueTask DisposeAsync() => StopAsync();

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (_stateGate)
        {
            _stopTask ??= StopCoreAsync();
            stopTask = _stopTask;
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask(stopTask.WaitAsync(cancellationToken))
            : new ValueTask(stopTask);
    }

    private async Task StopCoreAsync()
    {
        TransitionTo(SharpLinkConnectionState.Draining);
        _shutdownCts.Cancel();
        Volatile.Read(ref _readySignal).TrySetResult(true);

        var stoppingException = CreateConnectionClosedException("Client is stopping.");
        var sessionCts = Interlocked.Exchange(ref _sessionCts, null);
        if (sessionCts is not null)
        {
            await sessionCts.CancelAsync().ConfigureAwait(false);
            sessionCts.Dispose();
        }

        var session = Interlocked.Exchange(ref _session, null);
        _requestManager.FailAllPendingRequests(stoppingException);
        session?.StreamManager.CompleteAll(stoppingException);
        _serverStreamRequestIds.Clear();
        _locallyCanceledRequestIds.Clear();
        foreach (var requestId in _streamCallLifetimes.Keys)
            CompleteStreamLifetime(requestId);
        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);

        Task? connectTask;
        Task? reconnectTask;
        lock (_stateGate)
        {
            connectTask = _connectTask;
            reconnectTask = _reconnectTask;
        }
        await IgnoreExpectedStopExceptionAsync(connectTask).ConfigureAwait(false);
        if (!ReferenceEquals(reconnectTask, connectTask))
            await IgnoreExpectedStopExceptionAsync(reconnectTask).ConfigureAwait(false);
        await WaitForBackgroundTasksAsync().ConfigureAwait(false);

        _requestTimeoutScheduler.Dispose();
        _requestManager.Dispose();
        await transportFactory.DisposeAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
        TransitionTo(SharpLinkConnectionState.Stopped);
    }

    private static async Task IgnoreExpectedStopExceptionAsync(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException or SharpLinkException)
        {
        }
    }

    private void TrackBackgroundTask(Task task)
    {
        lock (_backgroundTasksGate)
            _backgroundTasks.Add(task);

        task.ContinueWith(
            static (completedTask, state) =>
            {
                var client = (SharpLinkClient)state!;
                lock (client._backgroundTasksGate)
                    client._backgroundTasks.Remove(completedTask);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WaitForBackgroundTasksAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_backgroundTasksGate)
                tasks = [.. _backgroundTasks];

            if (tasks.Length == 0)
                return;

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
            }
        }
    }

    private static SharpLinkException CreateAuthenticationRejectedException(string message)
        => new(SharpLinkErrorCode.AuthenticationRejected, message);

    private static SharpLinkException CreateConnectionClosedException(string message, Exception? innerException = null)
        => new(SharpLinkErrorCode.ConnectionClosed, message, innerException);

    private static SharpLinkException CreateHeartbeatTimeoutException(string message)
        => new(SharpLinkErrorCode.HeartbeatTimeout, message);

    private static SharpLinkException CreateProtocolViolationException(string message)
        => new(SharpLinkErrorCode.ProtocolViolation, message);

    private void TransitionTo(SharpLinkConnectionState state)
        => Interlocked.Exchange(ref _state, (int)state);

    private static TaskCompletionSource<bool> CreateReadySignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ResetReadySignal()
    {
        lock (_stateGate)
        {
            if (_readySignal.Task.IsCompleted)
                _readySignal = CreateReadySignal();
        }
    }

}
