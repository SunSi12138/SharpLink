


namespace SharpLink.Client;

internal sealed partial class SharpLinkClient(ITransport transport) : IRpcChannel, IDisposable, ISharpLinkClient, ISharpLinkClientDiagnostics
{
    private const string DefaultHandshakeMessage = "Password";
    private readonly StripedLongSet _serverStreamRequestIds = new();
    private readonly StripedLongSet _locallyCanceledRequestIds = new();
    private readonly RequestManager _requestManager = new();
    private readonly RequestTimeoutScheduler _requestTimeoutScheduler = new();
    private readonly CancellationTokenSource _lifecycleCts = new();
    private readonly Lock _backgroundTasksGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private IRpcSession? _session;
    private bool _disconnectHandled;
    private bool _disposed;
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private readonly bool _hasRequestTimeout;
    private readonly TimeSpan _requestTimeoutValue;
    private readonly string _handshakeMessage = DefaultHandshakeMessage;
    private readonly ILogger _logger = NullLogger<SharpLinkClient>.Instance;
    public Exception? LastConnectionException { get; private set; }

    public SharpLinkClient(
        ITransport transport,
        TimeSpan heartbeatInterval,
        TimeSpan heartbeatTimeout,
        TimeSpan? requestTimeout = null,
        string handshakeMessage = DefaultHandshakeMessage)
        : this(transport)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatTimeout, TimeSpan.Zero);
        ArgumentException.ThrowIfNullOrWhiteSpace(handshakeMessage);
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
    }

    public SharpLinkClient(
        ITransport transport,
        TimeSpan heartbeatInterval,
        TimeSpan heartbeatTimeout,
        ILoggerFactory loggerFactory,
        TimeSpan? requestTimeout = null,
        string handshakeMessage = DefaultHandshakeMessage)
        : this(transport,  heartbeatInterval, heartbeatTimeout, requestTimeout, handshakeMessage)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<SharpLinkClient>();
    }
    
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
            return;

        _lifecycleCts.Cancel();
        _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        WaitForBackgroundTasks();
        HandleDisconnected();
        _requestTimeoutScheduler.Dispose();
        _lifecycleCts.Dispose();
        transport.Dispose();
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

    private void WaitForBackgroundTasks()
    {
        Task[] tasks;
        lock (_backgroundTasksGate)
            tasks = [.. _backgroundTasks];

        if (tasks.Length == 0)
            return;

        try
        {
            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
        {
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

    private static SharpLinkException CreateRemoteErrorException(string message)
        => SharpLinkException.TryParsePayloadMessage(message, out var structuredException)
            ? structuredException!
            : new SharpLinkException(SharpLinkErrorCode.RemoteError, message);
}
