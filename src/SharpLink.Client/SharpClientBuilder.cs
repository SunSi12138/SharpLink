namespace SharpLink.Client;

public class SharpClientBuilder
{
    public static SharpClientBuilder Create() => new();
    
    
    private ITransport? _transport;
    private ILoggerFactory? _loggerFactory;
    private string _handshakeMessage = "Password";

    public SharpClientBuilder UseTransport(ITransport transport)
    {
        _transport = transport;
        return this;
    }

    public SharpClientBuilder UseAuthenticator(string handshakeMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handshakeMessage);
        _handshakeMessage = handshakeMessage;
        return this;
    }

    private Func<Type,IRpcCodec?>? _codecResolver;
    private TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private TimeSpan? _requestTimeout;
    private RpcSessionFlushOptions? _rpcSessionFlushOptions;

    public SharpClientBuilder UseSerializer(Func<Type,IRpcCodec?>? codecResolver)
    {
        _codecResolver = codecResolver;
        return this;
    }

    public SharpClientBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
        return this;
    }

    public SharpClientBuilder UseBufferWriterPool(Action<BufferWriterPoolOptions> configure)
    {
        BufferWriterPool.Configure(configure);
        return this;
    }

    public SharpClientBuilder UseStateStoreConcurrency(Action<RuntimeConcurrencyOptions> configure)
    {
        RuntimeConcurrency.Configure(configure);
        return this;
    }

    public void UseLoggerFactoryIfUnset(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory ??= loggerFactory;
    }

    public SharpClientBuilder UseHeartbeat(TimeSpan interval, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (timeout <= interval)
            throw new ArgumentException("Heartbeat timeout must be greater than interval.");

        _heartbeatInterval = interval;
        _heartbeatTimeout = timeout;
        return this;
    }

    public SharpClientBuilder UseHeartbeatInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        if (_heartbeatTimeout <= interval)
            throw new ArgumentException("Heartbeat timeout must be greater than interval.");

        _heartbeatInterval = interval;
        return this;
    }

    public SharpClientBuilder UseHeartbeatTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (timeout <= _heartbeatInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than interval.");

        _heartbeatTimeout = timeout;
        return this;
    }

    public SharpClientBuilder UseRequestTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _requestTimeout = timeout;
        return this;
    }

    public SharpClientBuilder DisableRequestTimeout()
    {
        _requestTimeout = null;
        return this;
    }

    public SharpClientBuilder UseRpcSessionFlush(int flushSizeThreshold, TimeSpan maxLatency)
    {
        _rpcSessionFlushOptions = RpcSessionFlushOptions.Create(flushSizeThreshold, maxLatency);
        return this;
    }
    
    public ISharpLinkClient Build()
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport must be set before building the server.");

        if (_codecResolver is not null)
            RpcCodecRegistry.Initialize(_codecResolver);

        if (_rpcSessionFlushOptions is not { } flushOptions)
            return new SharpLinkClient(
                _transport,
                _heartbeatInterval,
                _heartbeatTimeout,
                _loggerFactory ?? NullLoggerFactory.Instance,
                _requestTimeout,
                _handshakeMessage
            );
        
        if (_transport is not IRpcSessionFlushConfigurableTransport configurableTransport)
            throw new InvalidOperationException("Configured RPC session flush options, but transport does not support flush configuration.");
        
        configurableTransport.ConfigureRpcSessionFlush(flushOptions);

        return new SharpLinkClient(
            _transport,
            _heartbeatInterval,
            _heartbeatTimeout,
            _loggerFactory ?? NullLoggerFactory.Instance,
            _requestTimeout,
            _handshakeMessage
        );
    }
}
