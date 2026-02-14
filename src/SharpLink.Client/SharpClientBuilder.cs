namespace SharpLink.Client;

public class SharpClientBuilder
{
    public static SharpClientBuilder Create() => new();
    
    
    private ITransport? _transport;
    private readonly SharpLinkLoggingOptions _logging = new();

    public SharpClientBuilder UseTransport(ITransport transport)
    {
        _transport = transport;
        return this;
    }
    private ISerializer? _serializer;
    private TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private TimeSpan? _requestTimeout;
    private SharpLink.Runtime.RpcSessionFlushOptions? _rpcSessionFlushOptions;

    public SharpClientBuilder UseSerializer(ISerializer serializer)
    {
        _serializer = serializer;
        return this;
    }

    public SharpClientBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        _logging.UseLoggerFactory(loggerFactory);
        return this;
    }

    public SharpClientBuilder UseLogger(ILogger logger)
    {
        _logging.UseLogger(logger);
        return this;
    }

    public SharpClientBuilder UseMinimumLogLevel(LogLevel minimumLogLevel)
    {
        _logging.UseMinimumLogLevel(minimumLogLevel);
        return this;
    }

    public SharpClientBuilder UseLogging(Action<SharpLinkLoggingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_logging);
        return this;
    }

    public SharpClientBuilder UseBufferWriterPool(Action<BufferWriterPoolOptions> configure)
    {
        BufferWriterPool.Configure(configure);
        return this;
    }

    public void UseLoggerFactoryIfUnset(ILoggerFactory loggerFactory)
        => _logging.UseLoggerFactoryIfUnset(loggerFactory);

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
        _rpcSessionFlushOptions = SharpLink.Runtime.RpcSessionFlushOptions.Create(flushSizeThreshold, maxLatency);
        return this;
    }
    
    public ISharpLinkClient Build(string pipeName = "SharpLinkPipe")
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport must be set before building the server.");
        
        if (_serializer == null)
            throw new InvalidOperationException("Serializer must be set before building the server.");

        if (_rpcSessionFlushOptions is { } flushOptions)
        {
            if (_transport is not SharpLink.Runtime.IRpcSessionFlushConfigurableTransport configurableTransport)
                throw new InvalidOperationException("Configured RPC session flush options, but transport does not support flush configuration.");

            configurableTransport.ConfigureRpcSessionFlush(flushOptions);
        }
        
        return new SharpLinkClient(
            _transport,
            _serializer,
            _heartbeatInterval,
            _heartbeatTimeout,
            _logging,
            _requestTimeout
        );
    }
}
