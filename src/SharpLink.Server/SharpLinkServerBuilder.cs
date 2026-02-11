namespace SharpLink.Server;

public class SharpLinkServerBuilder : ISharpLinkServerBuilder
{
    public static SharpLinkServerBuilder Create() => new();

    private ITransport? _transport;
    private ISerializer? _serializer;
    private TimeSpan _heartbeatCheckInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private readonly Dictionary<long, (IRpcStub stub, object service)> _services = [];
    private readonly SharpLinkLoggingOptions _logging = new();

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
        _logging.UseLoggerFactory(loggerFactory);
        return this;
    }

    public SharpLinkServerBuilder UseLogger(ILogger logger)
    {
        _logging.UseLogger(logger);
        return this;
    }

    public SharpLinkServerBuilder UseMinimumLogLevel(LogLevel minimumLogLevel)
    {
        _logging.UseMinimumLogLevel(minimumLogLevel);
        return this;
    }

    public SharpLinkServerBuilder UseLogging(Action<SharpLinkLoggingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_logging);
        return this;
    }

    public SharpLinkServerBuilder UseBufferWriterPool(Action<BufferWriterPoolOptions> configure)
    {
        BufferWriterPool.Configure(configure);
        return this;
    }

    public void UseLoggerFactoryIfUnset(ILoggerFactory loggerFactory)
        => _logging.UseLoggerFactoryIfUnset(loggerFactory);

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
        if (checkInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(checkInterval));
        if (_heartbeatTimeout <= checkInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than check interval.");

        _heartbeatCheckInterval = checkInterval;
        return this;
    }

    public SharpLinkServerBuilder UseHeartbeatTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (timeout <= _heartbeatCheckInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than check interval.");

        _heartbeatTimeout = timeout;
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

        return new SharpLinkServer(
            _transport,
            _serializer,
            _services.ToFrozenDictionary(),
            _heartbeatCheckInterval,
            _heartbeatTimeout,
            _logging);
    }

}
