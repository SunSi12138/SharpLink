namespace SharpLink.Client;

public class SharpClientBuilder
{
    public static SharpClientBuilder Create() => new();
    
    
    private IClientTransportFactory? _transport;
    private ILoggerFactory? _loggerFactory;
    private ISharpLinkClientAuthenticator? _authenticator;
    private readonly List<ISharpLinkClientInterceptor> _interceptors = [];

    /// <summary>Uses an outbound transport factory owned by the built client.</summary>
    /// <param name="transport">The factory used for initial connections and reconnects.</param>
    public SharpClientBuilder UseTransport(IClientTransportFactory transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        return this;
    }

    /// <summary>Configures an instance-scoped client authentication payload provider.</summary>
    public SharpClientBuilder UseAuthenticator(ISharpLinkClientAuthenticator authenticator)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        return this;
    }

    /// <summary>Adds a client interceptor in registration order.</summary>
    public SharpClientBuilder AddInterceptor(ISharpLinkClientInterceptor interceptor)
    {
        _interceptors.Add(interceptor ?? throw new ArgumentNullException(nameof(interceptor)));
        return this;
    }

    private readonly SharpLinkRuntimeContextBuilder _runtimeContextBuilder = new();
    private TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private TimeSpan? _requestTimeout = TimeSpan.FromSeconds(30);
    private RpcSessionFlushOptions? _rpcSessionFlushOptions;
    private readonly SharpLinkConnectionPoolOptions _connectionPool = new();
    private bool _connectionPoolConfigured;

    /// <summary>Configures instance-scoped runtime behavior.</summary>
    public SharpClientBuilder UseRuntime(Action<SharpLinkRuntimeOptions> configure)
    {
        _runtimeContextBuilder.Configure(configure);
        return this;
    }

    /// <summary>Configures per-client protocol safety limits.</summary>
    public SharpClientBuilder UseProtocol(Action<SharpLinkProtocolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _runtimeContextBuilder.Configure(options => configure(options.Protocol));
        return this;
    }

    public SharpClientBuilder UseSerializer(Func<Type,IRpcCodec?>? codecResolver)
    {
        _runtimeContextBuilder.UseCodecResolver(codecResolver);
        return this;
    }

    /// <summary>Registers an explicit codec only for clients built by this builder.</summary>
    public SharpClientBuilder UseCodec<T>(IRpcCodec<T> codec)
    {
        _runtimeContextBuilder.AddCodec(codec);
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
        _runtimeContextBuilder.ConfigureBufferPool(configure);
        return this;
    }

    public SharpClientBuilder UseStateStoreConcurrency(Action<RuntimeConcurrencyOptions> configure)
    {
        _runtimeContextBuilder.ConfigureStateStores(configure);
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

    /// <summary>Configures the default timeout applied to unary calls without an earlier deadline.</summary>
    /// <param name="timeout">A positive timeout.</param>
    /// <returns>This builder.</returns>
    public SharpClientBuilder UseRequestTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _requestTimeout = timeout;
        return this;
    }

    /// <summary>Disables the client default request timeout.</summary>
    /// <remarks>Explicit call deadlines, call-option timeouts, and <c>TimeoutAttribute</c> still apply.</remarks>
    /// <returns>This builder.</returns>
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

    /// <summary>Configures the bounded connection pool for the selected endpoint.</summary>
    /// <param name="configure">Mutates builder-owned options that are frozen by <see cref="Build"/>.</param>
    public SharpClientBuilder UseConnectionPool(Action<SharpLinkConnectionPoolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_connectionPool);
        _connectionPoolConfigured = true;
        return this;
    }
    
    public ISharpLinkClient Build()
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport must be set before building the client.");

        var runtimeContext = _runtimeContextBuilder.Build();
        var protocolOptions = runtimeContext.Protocol;
        var connectionPool = CreateConnectionPoolSnapshot(runtimeContext);
        if (_transport is AnonymousPipeClientTransportFactory && connectionPool.MaxConnections != 1)
        {
            throw new InvalidOperationException(
                "Anonymous-pipe handle offers support exactly one client connection.");
        }

        return new SharpLinkClient(
            _transport,
            _heartbeatInterval,
            _heartbeatTimeout,
            _loggerFactory ?? NullLoggerFactory.Instance,
            _requestTimeout,
            _authenticator,
            protocolOptions,
            runtimeContext,
            _rpcSessionFlushOptions,
            connectionPool,
            _interceptors.ToArray()
        );
    }

    private SharpLinkConnectionPoolOptions CreateConnectionPoolSnapshot(SharpLinkRuntimeContext runtimeContext)
    {
        if (_connectionPoolConfigured)
            return _connectionPool.CloneValidated();

        var maxConnections = runtimeContext.Options.PerformanceProfile == SharpLinkPerformanceProfile.Throughput
            ? Math.Min(Environment.ProcessorCount, 4)
            : 1;
        return new SharpLinkConnectionPoolOptions
        {
            MinConnections = 1,
            MaxConnections = Math.Max(1, maxConnections)
        }.CloneValidated();
    }
}
