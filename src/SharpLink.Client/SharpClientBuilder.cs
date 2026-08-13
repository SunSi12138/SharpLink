namespace SharpLink.Client;

/// <summary>Configures and creates an independently owned SharpLink RPC client.</summary>
public class SharpClientBuilder
{
    private const string ConsumedBuilderMessage = "This SharpLink builder has already been consumed.";

    private readonly object _configurationGate = new();
    private readonly SharpLinkRuntimeContextBuilder _runtimeContextBuilder = new();
    private readonly List<ISharpLinkClientInterceptor> _interceptors = [];
    private readonly SharpLinkConnectionPoolOptions _connectionPool = new();
    private readonly SharpLinkClusterOptions _cluster = new();
    private readonly SharpLinkRetryOptions _retry = new();
    private readonly SharpLinkCircuitBreakerOptions _circuitBreaker = new();

    private BuilderState _state;
    private ClientTopologyDraft? _topology;
    private ClientRuntimeResources? _pendingResources;
    private ILoggerFactory? _loggerFactory;
    private ISharpLinkClientAuthenticator? _authenticator;
    private TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private TimeSpan? _requestTimeout = TimeSpan.FromSeconds(30);
    private RpcSessionFlushOptions? _rpcSessionFlushOptions;
    private bool _connectionPoolConfigured;
    private bool _clusterConfigured;
    private SharpLinkLoadBalancingStrategy _loadBalancingStrategy = SharpLinkLoadBalancingStrategy.PowerOfTwoChoices;
    private bool _loadBalancingConfigured;
    private ISharpLinkEndpointSelector? _endpointSelector;
    private bool _retryConfigured;
    private ISharpLinkRetryPolicy? _retryPolicy;
    private ISharpLinkEndpointAdmissionPolicy? _endpointAdmissionPolicy;
    private bool _circuitBreakerConfigured;
    private ISharpLinkReconnectJitter _reconnectJitter = RandomSharpLinkReconnectJitter.Instance;

    /// <summary>Creates a client builder with safe default runtime, heartbeat, timeout, and resilience settings.</summary>
    public static SharpClientBuilder Create() => new();

    /// <summary>Uses an outbound transport factory owned by the built client.</summary>
    /// <param name="transport">The factory used for initial connections and reconnects.</param>
    public SharpClientBuilder UseTransport(IClientTransportFactory transport)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(transport);
            SetTopology(new FixedTransportTopologyDraft(transport));
        });
        return this;
    }

    /// <summary>Gets the configured fixed transport factory when one has been selected.</summary>
    internal IClientTransportFactory? FixedTransportFactory
    {
        get
        {
            lock (_configurationGate)
                return _topology is FixedTransportTopologyDraft fixedTransport
                    ? fixedTransport.Transport
                    : null;
        }
    }

    /// <summary>Configures an instance-scoped client authentication payload provider.</summary>
    public SharpClientBuilder UseAuthenticator(ISharpLinkClientAuthenticator authenticator)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(authenticator);
            _authenticator = authenticator;
        });
        return this;
    }

    /// <summary>Adds a client interceptor in registration order.</summary>
    public SharpClientBuilder AddInterceptor(ISharpLinkClientInterceptor interceptor)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(interceptor);
            _interceptors.Add(interceptor);
        });
        return this;
    }

    /// <summary>Configures instance-scoped runtime behavior.</summary>
    public SharpClientBuilder UseRuntime(Action<SharpLinkRuntimeOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            _runtimeContextBuilder.Configure(configure);
        });
        return this;
    }

    /// <summary>Uses an application-owned time source for the built client. The client never disposes it.</summary>
    public SharpClientBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            _runtimeContextBuilder.UseTimeProvider(timeProvider);
        });
        return this;
    }

    /// <summary>
    /// Uses an isolated generated-manifest source for this Client build. The source is queried once
    /// by Compile and is not retained by the resulting Client.
    /// </summary>
    internal SharpClientBuilder UseGeneratedManifestSource(IGeneratedManifestSource source)
    {
        Configure(() => _runtimeContextBuilder.UseGeneratedManifestSource(source));
        return this;
    }

    /// <summary>Configures per-client protocol safety limits.</summary>
    public SharpClientBuilder UseProtocol(Action<SharpLinkProtocolOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            _runtimeContextBuilder.Configure(options => configure(options.Protocol));
        });
        return this;
    }

    /// <summary>Sets a fallback codec resolver scoped to clients built by this builder.</summary>
    internal SharpClientBuilder UseSerializer(Func<Type, IRpcCodec?>? codecResolver)
    {
        Configure(() => _runtimeContextBuilder.UseCodecResolver(codecResolver));
        return this;
    }

    /// <summary>Registers an explicit codec only for clients built by this builder.</summary>
    internal SharpClientBuilder UseCodec<T>(IRpcCodec<T> codec)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(codec);
            _runtimeContextBuilder.AddCodec(codec);
        });
        return this;
    }

    /// <summary>Uses the supplied application-owned logger factory.</summary>
    public SharpClientBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            _loggerFactory = loggerFactory;
        });
        return this;
    }

    /// <summary>Configures the instance-owned outbound buffer pool.</summary>
    public SharpClientBuilder UseBufferWriterPool(Action<BufferWriterPoolOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            _runtimeContextBuilder.ConfigureBufferPool(configure);
        });
        return this;
    }

    /// <summary>Configures striped state-store concurrency for this client.</summary>
    public SharpClientBuilder UseStateStoreConcurrency(Action<RuntimeConcurrencyOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            _runtimeContextBuilder.ConfigureStateStores(configure);
        });
        return this;
    }

    /// <summary>Sets an application-owned logger factory only when none was explicitly configured.</summary>
    public void UseLoggerFactoryIfUnset(ILoggerFactory loggerFactory)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            _loggerFactory ??= loggerFactory;
        });
    }

    /// <summary>Configures the heartbeat send interval and peer-liveness timeout.</summary>
    public SharpClientBuilder UseHeartbeat(TimeSpan interval, TimeSpan timeout)
    {
        Configure(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            if (timeout <= interval)
                throw new ArgumentException("Heartbeat timeout must be greater than interval.");
            _heartbeatInterval = interval;
            _heartbeatTimeout = timeout;
        });
        return this;
    }

    /// <summary>Configures how often the client sends heartbeat frames.</summary>
    public SharpClientBuilder UseHeartbeatInterval(TimeSpan interval)
    {
        Configure(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            if (_heartbeatTimeout <= interval)
                throw new ArgumentException("Heartbeat timeout must be greater than interval.");
            _heartbeatInterval = interval;
        });
        return this;
    }

    /// <summary>Configures how long peer inactivity is allowed before the connection is closed.</summary>
    public SharpClientBuilder UseHeartbeatTimeout(TimeSpan timeout)
    {
        Configure(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            if (timeout <= _heartbeatInterval)
                throw new ArgumentException("Heartbeat timeout must be greater than interval.");
            _heartbeatTimeout = timeout;
        });
        return this;
    }

    /// <summary>Configures the default timeout applied to unary calls without an earlier deadline.</summary>
    public SharpClientBuilder UseRequestTimeout(TimeSpan timeout)
    {
        Configure(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            _requestTimeout = timeout;
        });
        return this;
    }

    /// <summary>Disables the client default request timeout.</summary>
    public SharpClientBuilder DisableRequestTimeout()
    {
        Configure(() => _requestTimeout = null);
        return this;
    }

    /// <summary>Enables bounded send coalescing by byte threshold and maximum latency.</summary>
    public SharpClientBuilder UseRpcSessionFlush(int flushSizeThreshold, TimeSpan maxLatency)
    {
        Configure(() => _rpcSessionFlushOptions = RpcSessionFlushOptions.Create(flushSizeThreshold, maxLatency));
        return this;
    }

    /// <summary>Configures the bounded connection pool for the selected endpoint.</summary>
    public SharpClientBuilder UseConnectionPool(Action<SharpLinkConnectionPoolOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            configure(_connectionPool);
            _connectionPoolConfigured = true;
        });
        return this;
    }

    /// <summary>Uses one static endpoint and an endpoint-specific transport factory.</summary>
    public SharpClientBuilder UseEndpoint(
        SharpLinkEndpoint endpoint,
        SharpLinkEndpointTransportFactory transportFactory)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(transportFactory);
            SetTopology(new StaticEndpointsTopologyDraft([endpoint], transportFactory));
        });
        return this;
    }

    /// <summary>Uses a static endpoint collection and an endpoint-specific transport factory.</summary>
    public SharpClientBuilder UseEndpoints(
        IEnumerable<SharpLinkEndpoint> endpoints,
        SharpLinkEndpointTransportFactory transportFactory)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentNullException.ThrowIfNull(transportFactory);
            SetTopology(new StaticEndpointsTopologyDraft(endpoints, transportFactory));
        });
        return this;
    }

    /// <summary>Uses a client-owned resolver to maintain a dynamic endpoint topology.</summary>
    public SharpClientBuilder UseEndpointResolver(
        ISharpLinkEndpointResolver resolver,
        SharpLinkEndpointTransportFactory transportFactory)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(resolver);
            ArgumentNullException.ThrowIfNull(transportFactory);
            SetTopology(new DynamicResolverTopologyDraft(resolver, transportFactory));
        });
        return this;
    }

    /// <summary>Uses the built-in DNS resolver for a dynamic TCP endpoint topology.</summary>
    public SharpClientBuilder UseDnsEndpoints(
        string host,
        int port,
        SharpLinkEndpointTransportFactory transportFactory,
        Action<SharpLinkDnsResolverOptions>? configure = null)
    {
        Configure(() =>
        {
            EnsureTopologyAvailable(ClientTopologyKind.DynamicResolver);
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            if (port is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));
            ArgumentNullException.ThrowIfNull(transportFactory);
            var options = new SharpLinkDnsResolverOptions();
            configure?.Invoke(options);
            var frozenOptions = options.CloneValidated();
            SetTopology(new DynamicResolverTopologyDraft(
                new SharpLinkDnsEndpointResolver(host, port, frozenOptions, BclSharpLinkDnsQuery.Instance),
                transportFactory));
        });
        return this;
    }

    /// <summary>Configures the bounded resources used only by a multi-endpoint static cluster.</summary>
    public SharpClientBuilder UseCluster(Action<SharpLinkClusterOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            configure(_cluster);
            _clusterConfigured = true;
        });
        return this;
    }

    /// <summary>Selects a built-in static endpoint load-balancing strategy.</summary>
    public SharpClientBuilder UseLoadBalancing(SharpLinkLoadBalancingStrategy strategy)
    {
        Configure(() =>
        {
            if (!Enum.IsDefined(strategy))
                throw new ArgumentOutOfRangeException(nameof(strategy));
            if (_endpointSelector is not null)
                throw new InvalidOperationException("A custom endpoint selector is already configured.");
            _loadBalancingStrategy = strategy;
            _loadBalancingConfigured = true;
        });
        return this;
    }

    /// <summary>Uses a custom static endpoint selector.</summary>
    public SharpClientBuilder UseEndpointSelector(ISharpLinkEndpointSelector selector)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(selector);
            if (_loadBalancingConfigured)
                throw new InvalidOperationException("A built-in endpoint load-balancing strategy is already configured.");
            _endpointSelector = selector;
        });
        return this;
    }

    /// <summary>Enables the built-in retry policy for explicitly idempotent unary calls.</summary>
    public SharpClientBuilder UseRetry()
    {
        Configure(() =>
        {
            _retryConfigured = true;
            _retryPolicy = null;
        });
        return this;
    }

    /// <summary>Enables and configures the built-in retry policy for explicitly idempotent unary calls.</summary>
    public SharpClientBuilder UseRetry(Action<SharpLinkRetryOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            configure(_retry);
            _retryConfigured = true;
            _retryPolicy = null;
        });
        return this;
    }

    /// <summary>Enables a custom retry policy for explicitly idempotent unary calls.</summary>
    public SharpClientBuilder UseRetry(ISharpLinkRetryPolicy policy)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(policy);
            _retryPolicy = policy;
            _retryConfigured = true;
        });
        return this;
    }

    /// <summary>Uses a synchronous custom endpoint admission policy for cluster attempts.</summary>
    public SharpClientBuilder UseEndpointAdmission(ISharpLinkEndpointAdmissionPolicy policy)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(policy);
            if (_circuitBreakerConfigured)
                throw new InvalidOperationException("UseEndpointAdmission and UseCircuitBreaker are mutually exclusive.");
            _endpointAdmissionPolicy = policy;
        });
        return this;
    }

    /// <summary>Enables the built-in endpoint-generation circuit breaker for cluster attempts.</summary>
    public SharpClientBuilder UseCircuitBreaker(Action<SharpLinkCircuitBreakerOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            if (_endpointAdmissionPolicy is not null)
                throw new InvalidOperationException("UseEndpointAdmission and UseCircuitBreaker are mutually exclusive.");
            configure(_circuitBreaker);
            _circuitBreakerConfigured = true;
        });
        return this;
    }

    /// <summary>
    /// Sets the reconnect-jitter strategy for deterministic internal lifecycle tests. Production
    /// callers use the process-safe random strategy selected by the Builder default.
    /// </summary>
    internal SharpClientBuilder UseReconnectJitterForTesting(ISharpLinkReconnectJitter reconnectJitter)
    {
        Configure(() => _reconnectJitter = reconnectJitter ?? throw new ArgumentNullException(nameof(reconnectJitter)));
        return this;
    }

    /// <summary>Builds a normal client using one complete generated-manifest snapshot.</summary>
    public ISharpLinkClient Build()
        => Materialize(CompileForBuild());

    // Multi-cluster callers compile once, use this exact plan for budget validation, then materialize it.
    internal ClientBuildPlan CompileForMultiCluster(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> staticManifests)
        => CompileForBuild(new FixedGeneratedManifestSource(staticManifests));

    internal ISharpLinkClient MaterializeCompiledPlan(ClientBuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Materialize(plan);
    }

    internal void DiscardCompiledPlan(ClientBuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.MarkDiscarded();
        DisposeUnbuiltResources();
    }

    internal void DisposeUnbuiltResources()
    {
        ClientRuntimeResources? resources;
        lock (_configurationGate)
        {
            if (_state == BuilderState.Consumed)
                return;

            resources = _pendingResources ?? CreateRuntimeResources(_topology);
            _pendingResources = resources;
            _topology = null;
            _state = BuilderState.Consumed;
        }

        resources.DisposeUnmaterialized();
    }

    private ClientBuildPlan CompileForBuild()
        => CompileForBuildCore(_runtimeContextBuilder.Compile);

    private ClientBuildPlan CompileForBuild(IGeneratedManifestSource manifestSource)
    {
        ArgumentNullException.ThrowIfNull(manifestSource);
        return CompileForBuildCore(() => _runtimeContextBuilder.Compile(manifestSource));
    }

    private ClientBuildPlan CompileForBuildCore(
        Func<SharpLinkRuntimeContextBuildPlan> compileRuntimeContext)
    {
        ArgumentNullException.ThrowIfNull(compileRuntimeContext);
        BeginBuild();
        try
        {
            var plan = CompilePlan(compileRuntimeContext);
            lock (_configurationGate)
                _pendingResources = plan.Resources;
            return plan;
        }
        catch (Exception buildException)
        {
            try
            {
                DisposeUnbuiltResources();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(buildException, cleanupException);
            }
            throw;
        }
    }

    private ClientBuildPlan CompilePlan(
        Func<SharpLinkRuntimeContextBuildPlan> compileRuntimeContext)
    {
        var draft = _topology ?? throw new InvalidOperationException(
            "Transport, endpoint(s), or an endpoint resolver must be set before building the client.");
        var runtimeContext = compileRuntimeContext();
        var resources = CreateRuntimeResources(draft);
        var topology = CompileTopology(draft, runtimeContext, out var connectionPool, out var cluster);
        var retry = CreateRetryPlan();
        var circuitBreaker = CreateCircuitBreakerPlan();

        return new ClientBuildPlan(
            topology,
            resources,
            runtimeContext,
            _heartbeatInterval,
            _heartbeatTimeout,
            _requestTimeout,
            _rpcSessionFlushOptions,
            connectionPool,
            cluster,
            _loadBalancingStrategy,
            _endpointSelector,
            retry,
            _retryPolicy,
            circuitBreaker,
            _endpointAdmissionPolicy,
            _authenticator,
            _loggerFactory ?? NullLoggerFactory.Instance,
            [.. _interceptors],
            _reconnectJitter);
    }

    private ClientTopologyPlan CompileTopology(
        ClientTopologyDraft draft,
        SharpLinkRuntimeContextBuildPlan runtimeContext,
        out ClientConnectionPoolPlan connectionPool,
        out ClientClusterPlan? cluster)
    {
        switch (draft)
        {
            case FixedTransportTopologyDraft:
                connectionPool = CreateConnectionPoolPlan(runtimeContext);
                cluster = null;
                return new FixedTransportTopologyPlan();

            case StaticEndpointsTopologyDraft staticDraft:
                {
                    var endpoints = CreateEndpointSnapshot(staticDraft.Endpoints, allowEmpty: false);
                    if (endpoints.Length == 1)
                    {
                        if (_clusterConfigured)
                            throw new InvalidOperationException("UseCluster requires two or more endpoints.");
                        connectionPool = CreateConnectionPoolPlan(runtimeContext);
                        cluster = null;
                    }
                    else
                    {
                        if (_connectionPoolConfigured)
                            throw new InvalidOperationException("UseConnectionPool is only available for a fixed single endpoint.");
                        connectionPool = default;
                        cluster = CreateClusterPlan(_cluster.CloneValidated(endpoints.Length));
                    }
                    return new StaticEndpointsTopologyPlan(endpoints, staticDraft.TransportFactory);
                }

            case DynamicResolverTopologyDraft dynamicDraft:
                if (_connectionPoolConfigured)
                    throw new InvalidOperationException("UseConnectionPool is only available for a fixed single endpoint.");
                connectionPool = default;
                cluster = CreateClusterPlan(_cluster.CloneValidatedForDynamicResolver());
                return new DynamicResolverTopologyPlan(dynamicDraft.TransportFactory);

            default:
                throw new UnreachableException();
        }
    }

    private ISharpLinkClient Materialize(ClientBuildPlan plan)
    {
        using var transaction = new SynchronousBuildTransaction();
        var materializationStarted = false;
        try
        {
            plan.BeginMaterialization();
            materializationStarted = true;
            plan.Resources.RegisterWith(transaction);
            var runtimeContext = transaction.Own(
                plan.RuntimeContext.Materialize(),
                static context => context.Dispose(),
                SynchronousBuildResourceMetadata.FrameworkOwned("Client runtime context"));
            var client = MaterializeClient(plan, runtimeContext, transaction);
            transaction.Commit();
            plan.Resources.MarkTransferred();
            CompleteBuild();
            return client;
        }
        catch (Exception buildException)
        {
            if (materializationStarted)
                plan.Resources.MarkRolledBack();
            CompleteBuild();
            if (materializationStarted)
            {
                transaction.Rollback(buildException);
                throw new UnreachableException();
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(buildException).Throw();
            throw new UnreachableException();
        }
    }

    private static ISharpLinkClient MaterializeClient(
        ClientBuildPlan plan,
        SharpLinkRuntimeContext runtimeContext,
        SynchronousBuildTransaction transaction)
    {
        switch (plan.Topology)
        {
            case FixedTransportTopologyPlan:
                {
                    var transport = plan.Resources.DirectTransport ?? throw new InvalidOperationException(
                        "A fixed Client topology requires a direct transport resource.");
                    if (transport is IPerformanceProfileAwareTransport profileAwareTransport)
                        profileAwareTransport.BindPerformanceProfile(runtimeContext.PerformanceProfile);
                    var connectionPool = plan.ConnectionPool.CreateOptions();
                    if (transport is AnonymousPipeClientTransportFactory && connectionPool.MaxConnections != 1)
                    {
                        throw new InvalidOperationException(
                            "Anonymous-pipe handle offers support exactly one client connection.");
                    }
                    return CreateFixedClient(plan, transport, runtimeContext, connectionPool, fixedEndpoint: null);
                }

            case StaticEndpointsTopologyPlan staticTopology:
                {
                    if (staticTopology.EndpointCount == 1)
                    {
                        var endpoint = staticTopology[0];
                        var transport = CreateBuildTransportFactory(
                            endpoint,
                            staticTopology.TransportFactory,
                            runtimeContext,
                            transaction);
                        var connectionPool = plan.ConnectionPool.CreateOptions();
                        if (transport is AnonymousPipeClientTransportFactory && connectionPool.MaxConnections != 1)
                        {
                            throw new InvalidOperationException(
                                "Anonymous-pipe handle offers support exactly one client connection.");
                        }
                        return CreateFixedClient(plan, transport, runtimeContext, connectionPool, endpoint);
                    }

                    var configurations = new StaticEndpointConfiguration[staticTopology.EndpointCount];
                    for (var index = 0; index < configurations.Length; index++)
                    {
                        var endpoint = staticTopology[index];
                        var transport = CreateBuildTransportFactory(
                            endpoint,
                            staticTopology.TransportFactory,
                            runtimeContext,
                            transaction);
                        if (transport is AnonymousPipeClientTransportFactory)
                        {
                            throw new InvalidOperationException(
                                "Anonymous-pipe handle offers cannot be used by endpoint clusters.");
                        }
                        configurations[index] = new StaticEndpointConfiguration(endpoint, transport);
                    }
                    return CreateClusterClient(
                        plan,
                        configurations,
                        plan.Cluster ?? throw new InvalidOperationException("A static Client cluster requires cluster options."),
                        runtimeContext);
                }

            case DynamicResolverTopologyPlan dynamicTopology:
                {
                    var resolver = plan.Resources.DynamicResolver ?? throw new InvalidOperationException(
                        "A dynamic Client topology requires an endpoint resolver resource.");
                    if (resolver is ISharpLinkRuntimeTimeProviderAwareResolver timeProviderAware)
                        timeProviderAware.BindTimeProvider(runtimeContext.TimeProvider);
                    return CreateDynamicClusterClient(
                        plan,
                        resolver,
                        dynamicTopology.TransportFactory,
                        plan.Cluster ?? throw new InvalidOperationException("A dynamic Client cluster requires cluster options."),
                        runtimeContext);
                }

            default:
                throw new UnreachableException();
        }
    }

    private static ISharpLinkClient CreateFixedClient(
        ClientBuildPlan plan,
        IClientTransportFactory transport,
        SharpLinkRuntimeContext runtimeContext,
        SharpLinkConnectionPoolOptions connectionPool,
        SharpLinkEndpoint? fixedEndpoint)
        => CreateClient(
            plan,
            runtimeContext,
            transport,
            new FixedClientRuntimeTopologyComposition(fixedEndpoint),
            connectionPool);

    private static ISharpLinkClient CreateClusterClient(
        ClientBuildPlan plan,
        StaticEndpointConfiguration[] configurations,
        ClientClusterPlan cluster,
        SharpLinkRuntimeContext runtimeContext)
        => CreateClient(
            plan,
            runtimeContext,
            configurations[0].TransportFactory,
            new StaticClientRuntimeTopologyComposition(
                configurations,
                cluster.CreateOptions(),
                plan.LoadBalancingStrategy,
                plan.EndpointSelector),
            CreateDefaultConnectionPoolOptions());

    private static ISharpLinkClient CreateDynamicClusterClient(
        ClientBuildPlan plan,
        ISharpLinkEndpointResolver resolver,
        SharpLinkEndpointTransportFactory transportFactory,
        ClientClusterPlan cluster,
        SharpLinkRuntimeContext runtimeContext)
        => CreateClient(
            plan,
            runtimeContext,
            DynamicClusterTransportPlaceholder.Instance,
            new DynamicClientRuntimeTopologyComposition(
                resolver,
                transportFactory,
                cluster.CreateOptions(),
                plan.LoadBalancingStrategy,
                plan.EndpointSelector),
            CreateDefaultConnectionPoolOptions());

    private static ISharpLinkClient CreateClient(
        ClientBuildPlan plan,
        SharpLinkRuntimeContext runtimeContext,
        IClientTransportFactory transport,
        ClientRuntimeTopologyComposition topology,
        SharpLinkConnectionPoolOptions connectionPool)
    {
        var staticManifests = plan.CreateStaticManifestSnapshot();
        var requestTimeout = plan.RequestTimeout;
        var logger = plan.LoggerFactory.CreateLogger<SharpLinkClient>();
        var composition = new ClientRuntimeComposition(
            transport,
            topology,
            CreateReadinessConfiguration(plan),
            runtimeContext,
            staticManifests,
            SharpLinkClient.BuildStaticProxySnapshot(staticManifests),
            plan.HeartbeatInterval,
            plan.HeartbeatTimeout,
            requestTimeout.HasValue,
            requestTimeout.GetValueOrDefault(),
            plan.Authenticator,
            runtimeContext.Protocol.CloneValidated(),
            plan.RpcSessionFlushOptions,
            connectionPool,
            plan.CreateInterceptorSnapshot(),
            plan.Retry?.CreateOptions(),
            plan.RetryPolicy,
            CreateEndpointAdmissionPolicy(plan, runtimeContext),
            plan.ReconnectJitter,
            logger,
            SharpLinkClient.CreateFrameworkTaskSupervisor(logger));
        return new SharpLinkClient(composition);
    }

    private static ClientReadinessConfiguration CreateReadinessConfiguration(ClientBuildPlan plan)
    {
        return plan.Topology switch
        {
            FixedTransportTopologyPlan => new ClientReadinessConfiguration(1, 1, 1),
            StaticEndpointsTopologyPlan { EndpointCount: 1 } =>
                new ClientReadinessConfiguration(1, 1, 1),
            StaticEndpointsTopologyPlan staticTopology => CreateStaticReadinessConfiguration(
                staticTopology,
                plan.Cluster ?? throw new InvalidOperationException(
                    "A static Client cluster requires cluster options.")),
            DynamicResolverTopologyPlan => new ClientReadinessConfiguration(
                0,
                0,
                (plan.Cluster ?? throw new InvalidOperationException(
                    "A dynamic Client cluster requires cluster options.")).MinReadyEndpoints),
            _ => throw new UnreachableException()
        };
    }

    private static ClientReadinessConfiguration CreateStaticReadinessConfiguration(
        StaticEndpointsTopologyPlan topology,
        ClientClusterPlan cluster)
    {
        var target = Math.Min(cluster.MinReadyEndpoints, topology.EndpointCount);
        return new ClientReadinessConfiguration(topology.EndpointCount, target, target);
    }

    private static SharpLinkConnectionPoolOptions CreateDefaultConnectionPoolOptions()
        => new SharpLinkConnectionPoolOptions().CloneValidated();

    private static ISharpLinkEndpointAdmissionPolicy? CreateEndpointAdmissionPolicy(
        ClientBuildPlan plan,
        SharpLinkRuntimeContext runtimeContext)
        => plan.CircuitBreaker is { } circuitBreaker
            ? new SharpLinkCircuitBreaker(circuitBreaker.CreateOptions(), runtimeContext.TimeProvider)
            : plan.EndpointAdmissionPolicy;

    private static IClientTransportFactory CreateBuildTransportFactory(
        SharpLinkEndpoint endpoint,
        SharpLinkEndpointTransportFactory factory,
        SharpLinkRuntimeContext runtimeContext,
        SynchronousBuildTransaction transaction)
    {
        var transport = factory(endpoint) ?? throw new InvalidOperationException("Endpoint transport factory returned null.");
        transaction.Own(
            transport,
            static value => SharpLinkAsyncCleanup.DisposeSynchronously(value),
            SynchronousBuildResourceMetadata.FrameworkOwned("Client endpoint transport factory"));
        if (transport is IPerformanceProfileAwareTransport profileAware)
            profileAware.BindPerformanceProfile(runtimeContext.PerformanceProfile);
        return transport;
    }

    // Dynamic clusters materialize endpoint factories after Build has committed. Their local runtime cleanup
    // remains separate from the construction transaction and must not be used by builder materialization.
    internal static IClientTransportFactory CreateRuntimeTransportFactory(
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
                throw new UnreachableException();
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

    private ClientConnectionPoolPlan CreateConnectionPoolPlan(SharpLinkRuntimeContextBuildPlan runtimeContext)
    {
        var snapshot = _connectionPoolConfigured
            ? _connectionPool.CloneValidated()
            : new SharpLinkConnectionPoolOptions
            {
                MinConnections = 1,
                MaxConnections = Math.Max(
                    1,
                    runtimeContext.PerformanceProfile == SharpLinkPerformanceProfile.Throughput
                        ? Math.Min(Environment.ProcessorCount, 4)
                        : 1)
            }.CloneValidated();
        return new ClientConnectionPoolPlan(snapshot.MinConnections, snapshot.MaxConnections);
    }

    private ClientRetryPlan? CreateRetryPlan()
    {
        if (!_retryConfigured)
            return null;
        var snapshot = _retry.CloneValidated();
        return new ClientRetryPlan(
            snapshot.MaxAttempts,
            snapshot.InitialBackoff,
            snapshot.MaxBackoff,
            snapshot.JitterRatio);
    }

    private ClientCircuitBreakerPlan? CreateCircuitBreakerPlan()
    {
        if (!_circuitBreakerConfigured)
            return null;
        var snapshot = _circuitBreaker.CloneValidated();
        return new ClientCircuitBreakerPlan(
            snapshot.MinimumThroughput,
            snapshot.FailureRatio,
            snapshot.SamplingDuration,
            snapshot.BreakDuration,
            snapshot.HalfOpenMaxCalls);
    }

    private static ClientClusterPlan CreateClusterPlan(SharpLinkClusterOptions snapshot)
        => new(
            snapshot.MaxEndpoints,
            snapshot.MinReadyEndpoints,
            snapshot.MaxConnections,
            snapshot.MaxConnectionsPerEndpoint,
            snapshot.MaxRetiringConnections);

    private static ClientRuntimeResources CreateRuntimeResources(ClientTopologyDraft? topology)
        => topology switch
        {
            FixedTransportTopologyDraft fixedTransport => new ClientRuntimeResources(fixedTransport.Transport, null),
            DynamicResolverTopologyDraft dynamicResolver => new ClientRuntimeResources(null, dynamicResolver.Resolver),
            _ => new ClientRuntimeResources(null, null)
        };

    private void SetTopology(ClientTopologyDraft topology)
    {
        EnsureTopologyAvailable(topology.Kind);
        _topology = topology;
    }

    private void EnsureTopologyAvailable(ClientTopologyKind kind)
    {
        if (_topology is null)
            return;

        if (_topology.Kind != kind)
        {
            throw new InvalidOperationException(
                "UseTransport, UseEndpoint(s), and UseEndpointResolver are mutually exclusive.");
        }

        throw new InvalidOperationException("A Client topology has already been configured for this builder.");
    }

    private void Configure(Action configure)
    {
        lock (_configurationGate)
        {
            EnsureMutable();
            configure();
        }
    }

    private void BeginBuild()
    {
        lock (_configurationGate)
        {
            EnsureMutable();
            _state = BuilderState.Building;
        }
    }

    private void CompleteBuild()
    {
        lock (_configurationGate)
        {
            _topology = null;
            _pendingResources = null;
            _state = BuilderState.Consumed;
        }
    }

    private void EnsureMutable()
    {
        if (_state != BuilderState.Mutable)
            throw new InvalidOperationException(ConsumedBuilderMessage);
    }

    private enum BuilderState : byte
    {
        Mutable,
        Building,
        Consumed
    }
}
