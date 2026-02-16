


namespace SharpLink.Client;

internal sealed partial class SharpLinkClient(ITransport transport, ISerializer serializer) : IRpcChannel, IDisposable, ISharpLinkClient
{
    private readonly StripedLongSet _serverStreamRequestIds = new();
    private readonly StripedLongSet _locallyCanceledRequestIds = new();
    private readonly RequestManager _requestManager = new();
    private readonly RequestTimeoutScheduler _requestTimeoutScheduler = new();
    private IRpcSession? _session;
    private bool _disconnectHandled;
    private bool _disposed;
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private readonly bool _hasRequestTimeout;
    private readonly TimeSpan _requestTimeoutValue;
    private readonly ILogger _logger = NullLogger<SharpLinkClient>.Instance;

    public SharpLinkClient(ITransport transport, ISerializer serializer, TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout, TimeSpan? requestTimeout = null)
        : this(transport, serializer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatTimeout, TimeSpan.Zero);
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
    }

    public SharpLinkClient(
        ITransport transport,
        ISerializer serializer,
        TimeSpan heartbeatInterval,
        TimeSpan heartbeatTimeout,
        ILoggerFactory loggerFactory,
        TimeSpan? requestTimeout = null)
        : this(transport, serializer, heartbeatInterval, heartbeatTimeout, requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<SharpLinkClient>();
    }
    
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
            return;

        _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        HandleDisconnected(new ObjectDisposedException(nameof(SharpLinkClient)));
        _requestTimeoutScheduler.Dispose();
        transport.Dispose();
    }
}

