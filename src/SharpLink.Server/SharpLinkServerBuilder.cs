namespace SharpLink.Server;

public class SharpLinkServerBuilder : ISharpLinkServerBuilder
{
    public static SharpLinkServerBuilder Create() => new();

    private ITransport? _transport;
    private ISerializer? _serializer;
    private TimeSpan _heartbeatCheckInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private RpcSessionFlushOptions? _rpcSessionFlushOptions;
    private readonly Dictionary<long, (IRpcStub stub, object service)> _services = [];
    private ILoggerFactory? _loggerFactory;

    public SharpLinkServerBuilder UseTransport(ITransport transport)
    {
        _transport = transport;
        return this;
    }

    public SharpLinkServerBuilder UseSerializer(ISerializer serializer)
    {
        _serializer = serializer;
        return this;
    }

    public SharpLinkServerBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
        return this;
    }

    public SharpLinkServerBuilder UseBufferWriterPool(Action<BufferWriterPoolOptions> configure)
    {
        BufferWriterPool.Configure(configure);
        return this;
    }

    public SharpLinkServerBuilder UseStateStoreConcurrency(Action<RuntimeConcurrencyOptions> configure)
    {
        RuntimeConcurrency.Configure(configure);
        return this;
    }

    public void UseLoggerFactoryIfUnset(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory ??= loggerFactory;
    }

    public SharpLinkServerBuilder UseHeartbeat(TimeSpan checkInterval, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(checkInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        
        if (timeout <= checkInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than check interval.");

        _heartbeatCheckInterval = checkInterval;
        _heartbeatTimeout = timeout;
        return this;
    }

    public SharpLinkServerBuilder UseHeartbeatCheckInterval(TimeSpan checkInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(checkInterval, TimeSpan.Zero);
        if (_heartbeatTimeout <= checkInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than check interval.");

        _heartbeatCheckInterval = checkInterval;
        return this;
    }

    public SharpLinkServerBuilder UseHeartbeatTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (timeout <= _heartbeatCheckInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than check interval.");

        _heartbeatTimeout = timeout;
        return this;
    }

    public SharpLinkServerBuilder UseRpcSessionFlush(int flushSizeThreshold, TimeSpan maxLatency)
    {
        _rpcSessionFlushOptions = RpcSessionFlushOptions.Create(flushSizeThreshold, maxLatency);
        return this;
    }

    public SharpLinkServerBuilder AddService<TInterface, TService>()
        where TInterface : class, IService
        where TService : class, TInterface, new()
    {
        var service = new TService();
        if (!GeneratedStubRegistry.TryCreate(typeof(TService), out var stub) || stub is null)
            throw new InvalidOperationException($"Stub for service {typeof(TService).FullName} is not registered.");

        _services[stub.InterfaceHash] = (stub, service);
        return this;
    }

    public ISharpLinkServer Build()
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport must be set before building the server.");

        if (_serializer == null)
            throw new InvalidOperationException("Serializer must be set before building the server.");

        if (_rpcSessionFlushOptions is not { } flushOptions)
            return new SharpLinkServer(
                _transport,
                _serializer,
                _services.ToFrozenDictionary(),
                _heartbeatCheckInterval,
                _heartbeatTimeout,
                _loggerFactory ?? NullLoggerFactory.Instance);
        
        if (_transport is not IRpcSessionFlushConfigurableTransport configurableTransport)
            throw new InvalidOperationException("Configured RPC session flush options, but transport does not support flush configuration.");

        configurableTransport.ConfigureRpcSessionFlush(flushOptions);

        return new SharpLinkServer(
            _transport,
            _serializer,
            _services.ToFrozenDictionary(),
            _heartbeatCheckInterval,
            _heartbeatTimeout,
            _loggerFactory ?? NullLoggerFactory.Instance);
    }

}
