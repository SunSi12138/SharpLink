namespace SharpLink.Server;

public class SharpLinkServerBuilder : ISharpLinkServerBuilder
{
    private static readonly Func<string, SharpLinkAuthenticationResult> SDefaultAuthValidator =
        static _ => SharpLinkAuthenticationResult.Success;
    public static SharpLinkServerBuilder Create() => new();

    private IServerTransportListener? _transport;
    public IServerTransportListener? Transport=>_transport;
    private readonly SharpLinkRuntimeContextBuilder _runtimeContextBuilder = new();
    private TimeSpan _heartbeatCheckInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private RpcSessionFlushOptions? _rpcSessionFlushOptions;
    private readonly Dictionary<long, (IRpcStub stub, object service)> _services = [];
    private ILoggerFactory? _loggerFactory;
    private Func<string, SharpLinkAuthenticationResult> _authValidator = SDefaultAuthValidator;

    /// <summary>Uses a server listener owned by the built server.</summary>
    /// <param name="transport">The listener used to accept independent connections.</param>
    public SharpLinkServerBuilder UseTransport(IServerTransportListener transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        return this;
    }

    public SharpLinkServerBuilder UseAuthenticator(Func<string, bool> authValidator)
    {
        ArgumentNullException.ThrowIfNull(authValidator);
        _authValidator = message => authValidator(message)
            ? SharpLinkAuthenticationResult.Success
            : SharpLinkAuthenticationResult.Reject();
        return this;
    }

    public SharpLinkServerBuilder UseAuthenticator(Func<string, SharpLinkAuthenticationResult> authValidator)
    {
        ArgumentNullException.ThrowIfNull(authValidator);
        _authValidator = authValidator;
        return this;
    }

    public SharpLinkServerBuilder UseSerializer(Func<Type,IRpcCodec?>? codecResolver)
    {
        _runtimeContextBuilder.UseCodecResolver(codecResolver);
        return this;
    }

    /// <summary>Registers an explicit codec only for servers built by this builder.</summary>
    public SharpLinkServerBuilder UseCodec<T>(IRpcCodec<T> codec)
    {
        _runtimeContextBuilder.AddCodec(codec);
        return this;
    }

    /// <summary>Configures instance-scoped runtime behavior.</summary>
    public SharpLinkServerBuilder UseRuntime(Action<SharpLinkRuntimeOptions> configure)
    {
        _runtimeContextBuilder.Configure(configure);
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
        _runtimeContextBuilder.ConfigureBufferPool(configure);
        return this;
    }

    public SharpLinkServerBuilder UseStateStoreConcurrency(Action<RuntimeConcurrencyOptions> configure)
    {
        _runtimeContextBuilder.ConfigureStateStores(configure);
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

    /// <summary>Configures per-server protocol safety limits.</summary>
    public SharpLinkServerBuilder UseProtocol(Action<SharpLinkProtocolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _runtimeContextBuilder.Configure(options => configure(options.Protocol));
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

        var runtimeContext = _runtimeContextBuilder.Build();
        var protocolOptions = runtimeContext.Protocol;

        return new SharpLinkServer(
            _transport,
            _services.ToFrozenDictionary(),
            _heartbeatCheckInterval,
            _heartbeatTimeout,
            _loggerFactory ?? NullLoggerFactory.Instance,
            _authValidator,
            protocolOptions,
            runtimeContext,
            _rpcSessionFlushOptions);
    }

}
