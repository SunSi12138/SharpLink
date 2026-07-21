namespace SharpLink.Client;

public class SharpClientBuilder
{
    public static SharpClientBuilder Create() => new();
    
    
    private IClientTransportFactory? _transport;
    private IEnumerable<SharpLinkEndpoint>? _endpoints;
    private SharpLinkEndpointTransportFactory? _endpointTransportFactory;
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
    private readonly SharpLinkClusterOptions _cluster = new();
    private bool _clusterConfigured;
    private SharpLinkLoadBalancingStrategy _loadBalancingStrategy = SharpLinkLoadBalancingStrategy.PowerOfTwoChoices;
    private bool _loadBalancingConfigured;
    private ISharpLinkEndpointSelector? _endpointSelector;

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

    /// <summary>Uses one static endpoint and an endpoint-specific transport factory.</summary>
    /// <param name="endpoint">The endpoint copied and frozen during <see cref="Build"/>.</param>
    /// <param name="transportFactory">Creates the client-owned transport factory for the frozen endpoint.</param>
    public SharpClientBuilder UseEndpoint(
        SharpLinkEndpoint endpoint,
        SharpLinkEndpointTransportFactory transportFactory)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(transportFactory);
        _endpoints = [endpoint];
        _endpointTransportFactory = transportFactory;
        return this;
    }

    /// <summary>Uses a static endpoint collection and an endpoint-specific transport factory.</summary>
    /// <param name="endpoints">Endpoints enumerated once and frozen during <see cref="Build"/>.</param>
    /// <param name="transportFactory">Creates one client-owned transport factory per frozen endpoint.</param>
    public SharpClientBuilder UseEndpoints(
        IEnumerable<SharpLinkEndpoint> endpoints,
        SharpLinkEndpointTransportFactory transportFactory)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _endpointTransportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        return this;
    }

    /// <summary>Configures the bounded resources used only by a multi-endpoint static cluster.</summary>
    /// <param name="configure">Mutates builder-owned options frozen by <see cref="Build"/>.</param>
    public SharpClientBuilder UseCluster(Action<SharpLinkClusterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_cluster);
        _clusterConfigured = true;
        return this;
    }

    /// <summary>Selects a built-in static endpoint load-balancing strategy.</summary>
    /// <param name="strategy">The strategy used only by a multi-endpoint static cluster.</param>
    /// <exception cref="InvalidOperationException">A custom selector has already been configured.</exception>
    public SharpClientBuilder UseLoadBalancing(SharpLinkLoadBalancingStrategy strategy)
    {
        if (_endpointSelector is not null)
            throw new InvalidOperationException("A custom endpoint selector is already configured.");
        if (!Enum.IsDefined(strategy))
            throw new ArgumentOutOfRangeException(nameof(strategy));
        _loadBalancingStrategy = strategy;
        _loadBalancingConfigured = true;
        return this;
    }

    /// <summary>Uses a custom static endpoint selector.</summary>
    /// <param name="selector">A synchronous selector that returns a current candidate index.</param>
    /// <exception cref="InvalidOperationException">A built-in strategy was explicitly configured.</exception>
    public SharpClientBuilder UseEndpointSelector(ISharpLinkEndpointSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (_loadBalancingConfigured)
            throw new InvalidOperationException("A built-in endpoint load-balancing strategy is already configured.");
        _endpointSelector = selector;
        return this;
    }
    
    public ISharpLinkClient Build()
    {
        if (_transport is not null && _endpoints is not null)
            throw new InvalidOperationException("UseTransport and UseEndpoint(s) are mutually exclusive.");
        if (_transport is null && _endpoints is null)
            throw new InvalidOperationException("Transport or endpoint(s) must be set before building the client.");

        var runtimeContext = _runtimeContextBuilder.Build();
        var protocolOptions = runtimeContext.Protocol;
        if (_endpoints is not null)
        {
            var endpoints = CreateEndpointSnapshot(_endpoints);
            if (endpoints.Length == 1)
            {
                if (_clusterConfigured)
                    throw new InvalidOperationException("UseCluster requires two or more endpoints.");
                var transport = CreateTransportFactory(endpoints[0], _endpointTransportFactory!, runtimeContext);
                return CreateFixedClient(transport, runtimeContext, protocolOptions);
            }

            if (_connectionPoolConfigured)
                throw new InvalidOperationException("UseConnectionPool is only available for a fixed single endpoint.");
            var cluster = _cluster.CloneValidated(endpoints.Length);
            var configurations = new StaticEndpointConfiguration[endpoints.Length];
            var ownedFactories = new HashSet<IClientTransportFactory>(ReferenceEqualityComparer.Instance);
            try
            {
                for (var index = 0; index < endpoints.Length; index++)
                {
                    var factory = CreateTransportFactory(endpoints[index], _endpointTransportFactory!, runtimeContext);
                    if (!ownedFactories.Add(factory))
                    {
                        throw new InvalidOperationException(
                            "Each static endpoint must receive an independently owned transport factory.");
                    }
                    configurations[index] = new StaticEndpointConfiguration(
                        endpoints[index],
                        factory);
                }
                return CreateClusterClient(configurations, cluster, runtimeContext, protocolOptions);
            }
            catch
            {
                foreach (var factory in ownedFactories)
                    factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        }

        var fixedTransport = _transport!;
        if (fixedTransport is IPerformanceProfileAwareTransport profileAwareTransport)
            profileAwareTransport.BindPerformanceProfile(runtimeContext.Options.PerformanceProfile);
        var connectionPool = CreateConnectionPoolSnapshot(runtimeContext);
        if (fixedTransport is AnonymousPipeClientTransportFactory && connectionPool.MaxConnections != 1)
            throw new InvalidOperationException("Anonymous-pipe handle offers support exactly one client connection.");

        return CreateFixedClient(fixedTransport, runtimeContext, protocolOptions, connectionPool);
    }

    private ISharpLinkClient CreateFixedClient(
        IClientTransportFactory transport,
        SharpLinkRuntimeContext runtimeContext,
        SharpLinkProtocolOptions protocolOptions,
        SharpLinkConnectionPoolOptions? connectionPool = null)
    {
        return new SharpLinkClient(
            transport,
            _heartbeatInterval,
            _heartbeatTimeout,
            _loggerFactory ?? NullLoggerFactory.Instance,
            _requestTimeout,
            _authenticator,
            protocolOptions,
            runtimeContext,
            _rpcSessionFlushOptions,
            connectionPool ?? CreateConnectionPoolSnapshot(runtimeContext),
            _interceptors.ToArray()
        );
    }

    private ISharpLinkClient CreateClusterClient(
        StaticEndpointConfiguration[] configurations,
        SharpLinkClusterOptions cluster,
        SharpLinkRuntimeContext runtimeContext,
        SharpLinkProtocolOptions protocolOptions)
        => new SharpLinkClient(
            configurations[0].TransportFactory,
            _heartbeatInterval,
            _heartbeatTimeout,
            _loggerFactory ?? NullLoggerFactory.Instance,
            _requestTimeout,
            _authenticator,
            protocolOptions,
            runtimeContext,
            _rpcSessionFlushOptions,
            new SharpLinkConnectionPoolOptions(),
            _interceptors.ToArray(),
            configurations,
            cluster,
            _loadBalancingStrategy,
            _endpointSelector);

    private static IClientTransportFactory CreateTransportFactory(
        SharpLinkEndpoint endpoint,
        SharpLinkEndpointTransportFactory factory,
        SharpLinkRuntimeContext runtimeContext)
    {
        var transport = factory(endpoint) ?? throw new InvalidOperationException("Endpoint transport factory returned null.");
        if (transport is IPerformanceProfileAwareTransport profileAware)
            profileAware.BindPerformanceProfile(runtimeContext.Options.PerformanceProfile);
        return transport;
    }

    private static SharpLinkEndpoint[] CreateEndpointSnapshot(IEnumerable<SharpLinkEndpoint> source)
    {
        var endpoints = new List<SharpLinkEndpoint>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in source)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Id);
            if (endpoint.Id.Length > 256 || !StringComparer.Ordinal.Equals(endpoint.Id, endpoint.Id.Trim()))
                throw new ArgumentException("Endpoint IDs must be trimmed and at most 256 characters.", nameof(source));
            ArgumentNullException.ThrowIfNull(endpoint.Address);
            if (endpoint.Attributes.Count > 32)
                throw new ArgumentException("An endpoint supports at most 32 attributes.", nameof(source));
            var attributes = new Dictionary<string, string>(endpoint.Attributes.Count, StringComparer.Ordinal);
            foreach (var attribute in endpoint.Attributes)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Key);
                ArgumentNullException.ThrowIfNull(attribute.Value);
                if (attribute.Key.Length > 128 || attribute.Value.Length > 1024)
                    throw new ArgumentException("Endpoint attribute limits were exceeded.", nameof(source));
                attributes.Add(attribute.Key, attribute.Value);
            }
            if (!ids.Add(endpoint.Id))
                throw new ArgumentException("Endpoint IDs must be unique.", nameof(source));
            endpoints.Add(new SharpLinkEndpoint
            {
                Id = endpoint.Id,
                Address = endpoint.Address,
                Authority = endpoint.Authority,
                Attributes = attributes.ToFrozenDictionary(StringComparer.Ordinal)
            });
            if (endpoints.Count > SharpLinkClusterOptions.MaximumEndpoints)
                throw new ArgumentException("A static topology supports at most 64 endpoints.", nameof(source));
        }
        if (endpoints.Count == 0)
            throw new ArgumentException("At least one endpoint is required.", nameof(source));
        return [.. endpoints];
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
