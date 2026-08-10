namespace SharpLink.Client;

/// <summary>Configures and creates an independently owned SharpLink RPC client.</summary>
public class SharpClientBuilder
{
    /// <summary>Creates a client builder with safe default runtime, heartbeat, timeout, and resilience settings.</summary>
    public static SharpClientBuilder Create() => new();


    private IClientTransportFactory? _transport;
    private IEnumerable<SharpLinkEndpoint>? _endpoints;
    private SharpLinkEndpoint[]? _preflightEndpointSnapshot;
    private SharpLinkEndpointTransportFactory? _endpointTransportFactory;
    private ISharpLinkEndpointResolver? _endpointResolver;
    private SharpLinkEndpointTransportFactory? _resolverTransportFactory;
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
    private readonly SharpLinkRetryOptions _retry = new();
    private bool _retryConfigured;
    private ISharpLinkRetryPolicy? _retryPolicy;
    private ISharpLinkEndpointAdmissionPolicy? _endpointAdmissionPolicy;
    private readonly SharpLinkCircuitBreakerOptions _circuitBreaker = new();
    private bool _circuitBreakerConfigured;

    /// <summary>Configures instance-scoped runtime behavior.</summary>
    public SharpClientBuilder UseRuntime(Action<SharpLinkRuntimeOptions> configure)
    {
        _runtimeContextBuilder.Configure(configure);
        return this;
    }

    /// <summary>
    /// Uses an application-owned time source for the built client. The client never disposes it.
    /// </summary>
    public SharpClientBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        _runtimeContextBuilder.UseTimeProvider(timeProvider);
        return this;
    }

    /// <summary>Configures per-client protocol safety limits.</summary>
    public SharpClientBuilder UseProtocol(Action<SharpLinkProtocolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _runtimeContextBuilder.Configure(options => configure(options.Protocol));
        return this;
    }

    /// <summary>Sets a fallback codec resolver scoped to clients built by this builder.</summary>
    /// <param name="codecResolver">Returns a codec for a requested type, or <see langword="null"/> when unresolved.</param>
    public SharpClientBuilder UseSerializer(Func<Type, IRpcCodec?>? codecResolver)
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

    /// <summary>Uses the supplied application-owned logger factory.</summary>
    public SharpClientBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
        return this;
    }

    /// <summary>Configures the instance-owned outbound buffer pool.</summary>
    public SharpClientBuilder UseBufferWriterPool(Action<BufferWriterPoolOptions> configure)
    {
        _runtimeContextBuilder.ConfigureBufferPool(configure);
        return this;
    }

    /// <summary>Configures striped state-store concurrency for this client.</summary>
    public SharpClientBuilder UseStateStoreConcurrency(Action<RuntimeConcurrencyOptions> configure)
    {
        _runtimeContextBuilder.ConfigureStateStores(configure);
        return this;
    }

    /// <summary>Sets an application-owned logger factory only when none was explicitly configured.</summary>
    public void UseLoggerFactoryIfUnset(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory ??= loggerFactory;
    }

    /// <summary>Configures the heartbeat send interval and peer-liveness timeout.</summary>
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

    /// <summary>Configures how often the client sends heartbeat frames.</summary>
    public SharpClientBuilder UseHeartbeatInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        if (_heartbeatTimeout <= interval)
            throw new ArgumentException("Heartbeat timeout must be greater than interval.");

        _heartbeatInterval = interval;
        return this;
    }

    /// <summary>Configures how long peer inactivity is allowed before the connection is closed.</summary>
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

    /// <summary>Enables bounded send coalescing by byte threshold and maximum latency.</summary>
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
        _preflightEndpointSnapshot = null;
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
        _preflightEndpointSnapshot = null;
        _endpointTransportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        return this;
    }

    /// <summary>Uses a client-owned resolver to maintain a dynamic endpoint topology.</summary>
    /// <param name="resolver">The resolver disposed by the built client.</param>
    /// <param name="transportFactory">Creates one client-owned transport factory for each endpoint generation.</param>
    /// <remarks>
    /// This mode is mutually exclusive with <see cref="UseTransport"/> and <see cref="UseEndpoint"/>.
    /// The resolver supplies complete snapshots; its initial resolution and watch execute only after
    /// <see cref="ISharpLinkClient.ConnectAsync"/> is called.
    /// </remarks>
    public SharpClientBuilder UseEndpointResolver(
        ISharpLinkEndpointResolver resolver,
        SharpLinkEndpointTransportFactory transportFactory)
    {
        _endpointResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _resolverTransportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        return this;
    }

    /// <summary>Uses the built-in DNS resolver for a dynamic TCP endpoint topology.</summary>
    /// <param name="host">The DNS host name used as the default endpoint authority.</param>
    /// <param name="port">The TCP port from 1 through 65535.</param>
    /// <param name="transportFactory">Creates a client-owned transport factory for every discovered endpoint generation.</param>
    /// <param name="configure">Optionally configures refresh and address-family behavior.</param>
    /// <returns>This builder.</returns>
    public SharpClientBuilder UseDnsEndpoints(
        string host,
        int port,
        SharpLinkEndpointTransportFactory transportFactory,
        Action<SharpLinkDnsResolverOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        ArgumentNullException.ThrowIfNull(transportFactory);

        var options = new SharpLinkDnsResolverOptions();
        configure?.Invoke(options);
        return UseEndpointResolver(
            new SharpLinkDnsEndpointResolver(host, port, options),
            transportFactory);
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

    /// <summary>Enables the built-in retry policy for explicitly idempotent unary calls.</summary>
    /// <remarks>Retry is disabled by default. Streaming, one-way, and non-idempotent unary calls never retry.</remarks>
    public SharpClientBuilder UseRetry()
    {
        _retryConfigured = true;
        _retryPolicy = null;
        return this;
    }

    /// <summary>Enables and configures the built-in retry policy for explicitly idempotent unary calls.</summary>
    /// <param name="configure">Mutates builder-owned options frozen during <see cref="Build"/>.</param>
    public SharpClientBuilder UseRetry(Action<SharpLinkRetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_retry);
        _retryConfigured = true;
        _retryPolicy = null;
        return this;
    }

    /// <summary>Enables a custom retry policy for explicitly idempotent unary calls.</summary>
    /// <param name="policy">A synchronous policy that returns only a decision and delay.</param>
    public SharpClientBuilder UseRetry(ISharpLinkRetryPolicy policy)
    {
        _retryPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        _retryConfigured = true;
        return this;
    }

    /// <summary>Uses a synchronous custom endpoint admission policy for cluster attempts.</summary>
    /// <remarks>
    /// Endpoint admission and the built-in circuit breaker are alternative policies. Neither affects
    /// fixed <see cref="UseTransport(IClientTransportFactory)"/> mode because it has no endpoint topology.
    /// </remarks>
    public SharpClientBuilder UseEndpointAdmission(ISharpLinkEndpointAdmissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (_circuitBreakerConfigured)
            throw new InvalidOperationException("UseEndpointAdmission and UseCircuitBreaker are mutually exclusive.");
        _endpointAdmissionPolicy = policy;
        return this;
    }

    /// <summary>Enables the built-in endpoint-generation circuit breaker for cluster attempts.</summary>
    public SharpClientBuilder UseCircuitBreaker(Action<SharpLinkCircuitBreakerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_endpointAdmissionPolicy is not null)
            throw new InvalidOperationException("UseEndpointAdmission and UseCircuitBreaker are mutually exclusive.");
        configure(_circuitBreaker);
        _circuitBreakerConfigured = true;
        return this;
    }

    /// <summary>Builds a normal client using the complete generated-manifest catalog.</summary>
    public ISharpLinkClient Build() => BuildCore(staticManifests: null);

    internal int GetConfiguredMaximumConnections()
    {
        if (_endpointResolver is not null)
            return _cluster.MaxConnections;
        if (_endpoints is not null)
        {
            var endpoints = CreateEndpointSnapshot(_endpoints, allowEmpty: false);
            _preflightEndpointSnapshot = endpoints;
            if (endpoints.Length == 1)
                return GetFixedConnectionBudget();

            var cluster = _cluster.CloneValidated(endpoints.Length);
            return Math.Min(cluster.MaxConnections,
                checked(endpoints.Length * cluster.MaxConnectionsPerEndpoint));
        }
        return GetFixedConnectionBudget();
    }

    internal void DisposeUnbuiltResources()
    {
        var directTransport = _transport;
        var endpointResolver = _endpointResolver;
        _transport = null;
        _endpointResolver = null;

        List<Exception>? failures = null;
        if (directTransport is not null)
        {
            try { SharpLinkAsyncCleanup.DisposeSynchronously(directTransport); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (endpointResolver is not null && !ReferenceEquals(endpointResolver, directTransport))
        {
            try { SharpLinkAsyncCleanup.DisposeSynchronously(endpointResolver); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }

        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException(failures);
    }

    // Multi-cluster construction supplies a filtered immutable manifest snapshot here. Keeping this
    // decision at construction time preserves the ordinary client's hot path unchanged.
    internal ISharpLinkClient BuildCore(IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? staticManifests)
    {
        // Multi-cluster preflight enumerates a static endpoint source to calculate its exact
        // connection budget. Consume that one build-local snapshot, then clear it so later builds
        // retain the normal builder behavior of taking a fresh topology snapshot.
        var preflightEndpoints = staticManifests is null ? null : _preflightEndpointSnapshot;
        _preflightEndpointSnapshot = null;
        var modeCount = (_transport is null ? 0 : 1) + (_endpoints is null ? 0 : 1) + (_endpointResolver is null ? 0 : 1);
        if (modeCount > 1)
            throw new InvalidOperationException("UseTransport, UseEndpoint(s), and UseEndpointResolver are mutually exclusive.");
        if (modeCount == 0)
            throw new InvalidOperationException("Transport, endpoint(s), or an endpoint resolver must be set before building the client.");

        var directTransport = _transport;
        var endpointResolver = _endpointResolver;
        var runtimeContext = staticManifests is null
            ? _runtimeContextBuilder.Build()
            : _runtimeContextBuilder.Build(staticManifests);
        try
        {
            var client = BuildWithRuntimeContext(runtimeContext, staticManifests, preflightEndpoints);
            ReleaseTransferredBuilderResource(directTransport, endpointResolver);
            return client;
        }
        catch (Exception buildException)
        {
            ReleaseTransferredBuilderResource(directTransport, endpointResolver);
            ThrowAfterClientBuildRollback(
                buildException,
                directTransport,
                endpointResolver,
                runtimeContext);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowAfterClientBuildRollback(
        Exception buildException,
        IClientTransportFactory? directTransport,
        ISharpLinkEndpointResolver? endpointResolver,
        SharpLinkRuntimeContext runtimeContext)
    {
        List<Exception>? cleanupFailures = null;
        if (directTransport is not null)
        {
            try
            {
                SharpLinkAsyncCleanup.DisposeSynchronously(directTransport);
            }
            catch (Exception cleanupException)
            {
                (cleanupFailures ??= []).Add(cleanupException);
            }
        }
        if (endpointResolver is not null && !ReferenceEquals(endpointResolver, directTransport))
        {
            try
            {
                SharpLinkAsyncCleanup.DisposeSynchronously(endpointResolver);
            }
            catch (Exception cleanupException)
            {
                (cleanupFailures ??= []).Add(cleanupException);
            }
        }
        try
        {
            runtimeContext.Dispose();
        }
        catch (Exception cleanupException)
        {
            (cleanupFailures ??= []).Add(cleanupException);
        }
        if (cleanupFailures is null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(buildException).Throw();
        cleanupFailures!.Insert(0, buildException);
        throw new AggregateException(cleanupFailures);
    }

    private void ReleaseTransferredBuilderResource(
        IClientTransportFactory? directTransport,
        ISharpLinkEndpointResolver? endpointResolver)
    {
        if (directTransport is not null)
            _transport = null;
        if (endpointResolver is not null)
            _endpointResolver = null;
    }

    private ISharpLinkClient BuildWithRuntimeContext(
        SharpLinkRuntimeContext runtimeContext,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? staticManifests,
        SharpLinkEndpoint[]? preflightEndpoints)
    {
        var protocolOptions = runtimeContext.Protocol;
        if (_endpointResolver is not null)
        {
            if (_connectionPoolConfigured)
                throw new InvalidOperationException("UseConnectionPool is only available for a fixed single endpoint.");
            var cluster = _cluster.CloneValidatedForDynamicResolver();
            return CreateDynamicClusterClient(
                _endpointResolver,
                _resolverTransportFactory!,
                cluster,
                runtimeContext,
                protocolOptions,
                staticManifests);
        }

        if (_endpoints is not null)
        {
            var endpoints = preflightEndpoints ?? CreateEndpointSnapshot(_endpoints, allowEmpty: false);
            if (endpoints.Length == 1)
            {
                if (_clusterConfigured)
                    throw new InvalidOperationException("UseCluster requires two or more endpoints.");
                var transport = CreateTransportFactory(endpoints[0], _endpointTransportFactory!, runtimeContext);
                try
                {
                    var singleEndpointPool = CreateConnectionPoolSnapshot(runtimeContext);
                    if (transport is AnonymousPipeClientTransportFactory && singleEndpointPool.MaxConnections != 1)
                    {
                        throw new InvalidOperationException(
                            "Anonymous-pipe handle offers support exactly one client connection.");
                    }
                    return CreateFixedClient(
                        transport,
                        runtimeContext,
                        protocolOptions,
                        singleEndpointPool,
                        fixedEndpoint: endpoints[0],
                        staticManifests: staticManifests);
                }
                catch (Exception buildException)
                {
                    try
                    {
                        SharpLinkAsyncCleanup.DisposeSynchronously(transport);
                    }
                    catch (Exception cleanupException)
                    {
                        throw new AggregateException(buildException, cleanupException);
                    }
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(buildException).Throw();
                    throw new System.Diagnostics.UnreachableException();
                }
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
                    if (factory is AnonymousPipeClientTransportFactory)
                    {
                        throw new InvalidOperationException(
                            "Anonymous-pipe handle offers cannot be used by endpoint clusters.");
                    }
                    configurations[index] = new StaticEndpointConfiguration(
                        endpoints[index],
                        factory);
                }
                return CreateClusterClient(configurations, cluster, runtimeContext, protocolOptions, staticManifests);
            }
            catch (Exception buildException)
            {
                List<Exception>? cleanupFailures = null;
                foreach (var factory in ownedFactories)
                {
                    try { SharpLinkAsyncCleanup.DisposeSynchronously(factory); }
                    catch (Exception exception) { (cleanupFailures ??= []).Add(exception); }
                }
                if (cleanupFailures is null)
                    throw;
                cleanupFailures.Insert(0, buildException);
                throw new AggregateException(cleanupFailures);
            }
        }

        var fixedTransport = _transport!;
        if (fixedTransport is IPerformanceProfileAwareTransport profileAwareTransport)
            profileAwareTransport.BindPerformanceProfile(runtimeContext.PerformanceProfile);
        var connectionPool = CreateConnectionPoolSnapshot(runtimeContext);
        if (fixedTransport is AnonymousPipeClientTransportFactory && connectionPool.MaxConnections != 1)
            throw new InvalidOperationException("Anonymous-pipe handle offers support exactly one client connection.");

        return CreateFixedClient(fixedTransport, runtimeContext, protocolOptions, connectionPool,
            staticManifests: staticManifests);
    }

    private ISharpLinkClient CreateFixedClient(
        IClientTransportFactory transport,
        SharpLinkRuntimeContext runtimeContext,
        SharpLinkProtocolOptions protocolOptions,
        SharpLinkConnectionPoolOptions? connectionPool = null,
        SharpLinkEndpoint? fixedEndpoint = null,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? staticManifests = null)
    {
        return new SharpLinkClient(
            transport,
            _heartbeatInterval,
            _heartbeatTimeout,
            _loggerFactory ?? NullLoggerFactory.Instance,
            runtimeContext,
            _requestTimeout,
            _authenticator,
            protocolOptions,
            _rpcSessionFlushOptions,
            connectionPool ?? CreateConnectionPoolSnapshot(runtimeContext),
            _interceptors.ToArray(),
            fixedEndpoint: fixedEndpoint,
            retryOptions: CreateRetryOptions(),
            retryPolicy: _retryPolicy,
            endpointAdmissionPolicy: CreateEndpointAdmissionPolicy(),
            staticManifests: staticManifests
        );
    }

    private ISharpLinkClient CreateClusterClient(
        StaticEndpointConfiguration[] configurations,
        SharpLinkClusterOptions cluster,
        SharpLinkRuntimeContext runtimeContext,
        SharpLinkProtocolOptions protocolOptions,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? staticManifests)
        => new SharpLinkClient(
            configurations[0].TransportFactory,
            _heartbeatInterval,
            _heartbeatTimeout,
            _loggerFactory ?? NullLoggerFactory.Instance,
            runtimeContext,
            _requestTimeout,
            _authenticator,
            protocolOptions,
            _rpcSessionFlushOptions,
            new SharpLinkConnectionPoolOptions(),
            _interceptors.ToArray(),
            configurations,
            cluster,
            _loadBalancingStrategy,
            _endpointSelector,
            retryOptions: CreateRetryOptions(),
            retryPolicy: _retryPolicy,
            endpointAdmissionPolicy: CreateEndpointAdmissionPolicy(),
            staticManifests: staticManifests);

    private ISharpLinkClient CreateDynamicClusterClient(
        ISharpLinkEndpointResolver resolver,
        SharpLinkEndpointTransportFactory transportFactory,
        SharpLinkClusterOptions cluster,
        SharpLinkRuntimeContext runtimeContext,
        SharpLinkProtocolOptions protocolOptions,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? staticManifests)
        => new SharpLinkClient(
            DynamicClusterTransportPlaceholder.Instance,
            _heartbeatInterval,
            _heartbeatTimeout,
            _loggerFactory ?? NullLoggerFactory.Instance,
            runtimeContext,
            _requestTimeout,
            _authenticator,
            protocolOptions,
            _rpcSessionFlushOptions,
            new SharpLinkConnectionPoolOptions(),
            _interceptors.ToArray(),
            dynamicResolver: resolver,
            dynamicTransportFactory: transportFactory,
            clusterOptions: cluster,
            loadBalancingStrategy: _loadBalancingStrategy,
            endpointSelector: _endpointSelector,
            retryOptions: CreateRetryOptions(),
            retryPolicy: _retryPolicy,
            endpointAdmissionPolicy: CreateEndpointAdmissionPolicy(),
            staticManifests: staticManifests);

    private SharpLinkRetryOptions? CreateRetryOptions()
        => _retryConfigured ? _retry.CloneValidated() : null;

    private ISharpLinkEndpointAdmissionPolicy? CreateEndpointAdmissionPolicy()
        => _circuitBreakerConfigured
            ? new SharpLinkCircuitBreaker(_circuitBreaker.CloneValidated())
            : _endpointAdmissionPolicy;

    internal static IClientTransportFactory CreateTransportFactory(
        SharpLinkEndpoint endpoint,
        SharpLinkEndpointTransportFactory factory,
        SharpLinkRuntimeContext runtimeContext)
    {
        var transport = factory(endpoint) ?? throw new InvalidOperationException("Endpoint transport factory returned null.");
        if (transport is IPerformanceProfileAwareTransport profileAware)
        {
            try
            {
                profileAware.BindPerformanceProfile(runtimeContext.PerformanceProfile);
            }
            catch (Exception bindingException)
            {
                try
                {
                    SharpLinkAsyncCleanup.DisposeSynchronously(transport);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(bindingException, cleanupException);
                }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(bindingException).Throw();
                throw new System.Diagnostics.UnreachableException();
            }
        }
        return transport;
    }

    internal static SharpLinkEndpoint[] CreateEndpointSnapshot(
        IEnumerable<SharpLinkEndpoint> source,
        bool allowEmpty)
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
            if (endpoint.Attributes is null)
                throw new ArgumentException("Endpoint attributes cannot be null.", nameof(source));
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
        if (!allowEmpty && endpoints.Count == 0)
            throw new ArgumentException("At least one endpoint is required.", nameof(source));
        return [.. endpoints];
    }

    private SharpLinkConnectionPoolOptions CreateConnectionPoolSnapshot(SharpLinkRuntimeContext runtimeContext)
    {
        if (_connectionPoolConfigured)
            return _connectionPool.CloneValidated();

        var maxConnections = runtimeContext.PerformanceProfile == SharpLinkPerformanceProfile.Throughput
            ? Math.Min(Environment.ProcessorCount, 4)
            : 1;
        return new SharpLinkConnectionPoolOptions
        {
            MinConnections = 1,
            MaxConnections = Math.Max(1, maxConnections)
        }.CloneValidated();
    }

    private int GetFixedConnectionBudget()
    {
        if (_connectionPoolConfigured)
            return _connectionPool.CloneValidated().MaxConnections;

        using var runtimeContext = _runtimeContextBuilder.Build(includeGeneratedAssemblyCatalog: false);
        return runtimeContext.PerformanceProfile == SharpLinkPerformanceProfile.Throughput
            ? Math.Max(1, Math.Min(Environment.ProcessorCount, 4))
            : 1;
    }
}
