namespace SharpLink.Client;

/// <summary>Identifies the one client topology selected during configuration.</summary>
internal enum ClientTopologyKind : byte
{
    FixedTransport,
    StaticEndpoints,
    DynamicResolver
}

/// <summary>Mutable-builder topology state. Exactly one instance is present while a builder is mutable.</summary>
internal abstract class ClientTopologyDraft
{
    internal abstract ClientTopologyKind Kind { get; }
}

internal sealed class FixedTransportTopologyDraft(IClientTransportFactory transport) : ClientTopologyDraft
{
    internal IClientTransportFactory Transport { get; } = transport ?? throw new ArgumentNullException(nameof(transport));
    internal override ClientTopologyKind Kind => ClientTopologyKind.FixedTransport;
}

internal sealed class StaticEndpointsTopologyDraft(
    IEnumerable<SharpLinkEndpoint> endpoints,
    SharpLinkEndpointTransportFactory transportFactory) : ClientTopologyDraft
{
    internal IEnumerable<SharpLinkEndpoint> Endpoints { get; } = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
    internal SharpLinkEndpointTransportFactory TransportFactory { get; } = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    internal override ClientTopologyKind Kind => ClientTopologyKind.StaticEndpoints;
}

internal sealed class DynamicResolverTopologyDraft(
    ISharpLinkEndpointResolver resolver,
    SharpLinkEndpointTransportFactory transportFactory) : ClientTopologyDraft
{
    internal ISharpLinkEndpointResolver Resolver { get; } = resolver ?? throw new ArgumentNullException(nameof(resolver));
    internal SharpLinkEndpointTransportFactory TransportFactory { get; } = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    internal override ClientTopologyKind Kind => ClientTopologyKind.DynamicResolver;
}

/// <summary>Immutable topology data used after Client plan compilation.</summary>
internal abstract class ClientTopologyPlan
{
    internal abstract ClientTopologyKind Kind { get; }
}

internal sealed class FixedTransportTopologyPlan : ClientTopologyPlan
{
    internal override ClientTopologyKind Kind => ClientTopologyKind.FixedTransport;
}

internal sealed class StaticEndpointsTopologyPlan : ClientTopologyPlan
{
    private readonly SharpLinkEndpoint[] _endpoints;

    internal StaticEndpointsTopologyPlan(
        SharpLinkEndpoint[] endpoints,
        SharpLinkEndpointTransportFactory transportFactory)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        TransportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _endpoints = [.. endpoints];
    }

    internal override ClientTopologyKind Kind => ClientTopologyKind.StaticEndpoints;

    /// <summary>Gets the number of endpoints frozen by Compile.</summary>
    internal int EndpointCount => _endpoints.Length;

    internal SharpLinkEndpoint this[int index] => _endpoints[index];

    internal SharpLinkEndpointTransportFactory TransportFactory { get; }
}

internal sealed class DynamicResolverTopologyPlan(
    SharpLinkEndpointTransportFactory transportFactory) : ClientTopologyPlan
{
    internal SharpLinkEndpointTransportFactory TransportFactory { get; } = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    internal override ClientTopologyKind Kind => ClientTopologyKind.DynamicResolver;
}

/// <summary>
/// Holds the pre-existing resources whose ownership changes only after a Client is successfully
/// constructed. Direct transports and endpoint resolvers are framework-owned after configuration;
/// endpoint factories created from static endpoints are registered separately during materialization.
/// </summary>
internal sealed class ClientRuntimeResources
{
    private int _state;

    internal ClientRuntimeResources(
        IClientTransportFactory? directTransport,
        ISharpLinkEndpointResolver? dynamicResolver)
    {
        DirectTransport = directTransport;
        DynamicResolver = dynamicResolver;
    }

    /// <summary>Framework-owned by the completed Client; transaction rollback disposes it on failure.</summary>
    internal IClientTransportFactory? DirectTransport { get; }

    /// <summary>Framework-owned by the completed Client; transaction rollback disposes it on failure.</summary>
    internal ISharpLinkEndpointResolver? DynamicResolver { get; }

    internal void RegisterWith(SynchronousBuildTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        EnsurePending();
        if (DirectTransport is not null)
        {
            transaction.Own(
                DirectTransport,
                static transport => SharpLinkAsyncCleanup.DisposeSynchronously(transport),
                SynchronousBuildResourceMetadata.FrameworkOwned("Client direct transport"));
        }
        if (DynamicResolver is not null && !ReferenceEquals(DynamicResolver, DirectTransport))
        {
            transaction.Own(
                DynamicResolver,
                static resolver => SharpLinkAsyncCleanup.DisposeSynchronously(resolver),
                SynchronousBuildResourceMetadata.FrameworkOwned("Client endpoint resolver"));
        }
    }

    internal void MarkTransferred()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("Client runtime resources have already reached a terminal state.");
    }

    internal void MarkRolledBack()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
            throw new InvalidOperationException("Client runtime resources have already reached a terminal state.");
    }

    internal void DisposeUnmaterialized()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
            return;

        using var transaction = new SynchronousBuildTransaction();
        if (DirectTransport is not null)
        {
            transaction.Own(
                DirectTransport,
                static transport => SharpLinkAsyncCleanup.DisposeSynchronously(transport),
                SynchronousBuildResourceMetadata.FrameworkOwned("unbuilt Client direct transport"));
        }
        if (DynamicResolver is not null && !ReferenceEquals(DynamicResolver, DirectTransport))
        {
            transaction.Own(
                DynamicResolver,
                static resolver => SharpLinkAsyncCleanup.DisposeSynchronously(resolver),
                SynchronousBuildResourceMetadata.FrameworkOwned("unbuilt Client endpoint resolver"));
        }
        transaction.Rollback();
    }

    private void EnsurePending()
    {
        if (Volatile.Read(ref _state) != 0)
            throw new InvalidOperationException("Client runtime resources have already reached a terminal state.");
    }
}

/// <summary>Frozen client construction inputs. It owns no cleanup behavior and is materialized once.</summary>
internal sealed class ClientBuildPlan
{
    private readonly ISharpLinkClientInterceptor[] _interceptors;
    private int _materializationState;

    internal ClientBuildPlan(
        ClientTopologyPlan topology,
        ClientRuntimeResources resources,
        SharpLinkRuntimeContextBuildPlan runtimeContext,
        SharpLinkGeneratedManifestSource manifestSource,
        TimeSpan heartbeatInterval,
        TimeSpan heartbeatTimeout,
        TimeSpan? requestTimeout,
        RpcSessionFlushOptions? rpcSessionFlushOptions,
        ClientConnectionPoolPlan connectionPool,
        ClientClusterPlan? cluster,
        SharpLinkLoadBalancingStrategy loadBalancingStrategy,
        ISharpLinkEndpointSelector? endpointSelector,
        ClientRetryPlan? retry,
        ISharpLinkRetryPolicy? retryPolicy,
        ClientCircuitBreakerPlan? circuitBreaker,
        ISharpLinkEndpointAdmissionPolicy? endpointAdmissionPolicy,
        ISharpLinkClientAuthenticator? authenticator,
        ILoggerFactory loggerFactory,
        ISharpLinkClientInterceptor[] interceptors,
        ISharpLinkReconnectJitter reconnectJitter)
    {
        Topology = topology ?? throw new ArgumentNullException(nameof(topology));
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        RuntimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        ManifestSource = manifestSource ?? throw new ArgumentNullException(nameof(manifestSource));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatTimeout, TimeSpan.Zero);
        if (heartbeatTimeout <= heartbeatInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than interval.");
        if (requestTimeout is { } timeout)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        HeartbeatInterval = heartbeatInterval;
        HeartbeatTimeout = heartbeatTimeout;
        RequestTimeout = requestTimeout;
        RpcSessionFlushOptions = rpcSessionFlushOptions;
        ConnectionPool = connectionPool;
        Cluster = cluster;
        LoadBalancingStrategy = loadBalancingStrategy;
        EndpointSelector = endpointSelector;
        Retry = retry;
        RetryPolicy = retryPolicy;
        CircuitBreaker = circuitBreaker;
        EndpointAdmissionPolicy = endpointAdmissionPolicy;
        Authenticator = authenticator;
        LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _interceptors = interceptors is { Length: > 0 } ? [.. interceptors] : [];
        ReconnectJitter = reconnectJitter ?? throw new ArgumentNullException(nameof(reconnectJitter));
    }

    internal ClientTopologyPlan Topology { get; }
    internal ClientRuntimeResources Resources { get; }
    internal SharpLinkRuntimeContextBuildPlan RuntimeContext { get; }
    internal SharpLinkGeneratedManifestSource ManifestSource { get; }
    internal TimeSpan HeartbeatInterval { get; }
    internal TimeSpan HeartbeatTimeout { get; }
    internal TimeSpan? RequestTimeout { get; }
    internal RpcSessionFlushOptions? RpcSessionFlushOptions { get; }
    internal ClientConnectionPoolPlan ConnectionPool { get; }
    internal ClientClusterPlan? Cluster { get; }
    internal SharpLinkLoadBalancingStrategy LoadBalancingStrategy { get; }
    internal ISharpLinkEndpointSelector? EndpointSelector { get; }
    internal ClientRetryPlan? Retry { get; }
    internal ISharpLinkRetryPolicy? RetryPolicy { get; }
    internal ClientCircuitBreakerPlan? CircuitBreaker { get; }
    internal ISharpLinkEndpointAdmissionPolicy? EndpointAdmissionPolicy { get; }
    internal ISharpLinkClientAuthenticator? Authenticator { get; }
    internal ILoggerFactory LoggerFactory { get; }
    internal ISharpLinkReconnectJitter ReconnectJitter { get; }

    internal int MaximumConnections => Topology switch
    {
        FixedTransportTopologyPlan => ConnectionPool.MaxConnections,
        DynamicResolverTopologyPlan => Cluster?.MaxConnections ?? throw new InvalidOperationException("A dynamic topology requires cluster options."),
        StaticEndpointsTopologyPlan staticTopology when staticTopology.EndpointCount == 1 => ConnectionPool.MaxConnections,
        StaticEndpointsTopologyPlan staticTopology => Math.Min(
            Cluster?.MaxConnections ?? throw new InvalidOperationException("A static cluster requires cluster options."),
            checked(staticTopology.EndpointCount * (Cluster?.MaxConnectionsPerEndpoint ?? 0))),
        _ => throw new UnreachableException()
    };

    internal ISharpLinkClientInterceptor[] CreateInterceptorSnapshot()
        => _interceptors.Length == 0 ? [] : [.. _interceptors];

    internal IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CreateStaticManifestSnapshot()
        => ManifestSource.CreateMaterializationSnapshot();

    internal void BeginMaterialization()
    {
        if (Interlocked.CompareExchange(ref _materializationState, 1, 0) != 0)
            throw new InvalidOperationException("This Client build plan has already been materialized or discarded.");
    }

    internal void MarkDiscarded()
    {
        if (Interlocked.CompareExchange(ref _materializationState, 2, 0) != 0)
            throw new InvalidOperationException("This Client build plan has already been materialized or discarded.");
    }
}

internal readonly record struct ClientConnectionPoolPlan(int MinConnections, int MaxConnections)
{
    internal SharpLinkConnectionPoolOptions CreateOptions()
        => new()
        {
            MinConnections = MinConnections,
            MaxConnections = MaxConnections
        };
}

internal readonly record struct ClientClusterPlan(
    int MaxEndpoints,
    int MinReadyEndpoints,
    int MaxConnections,
    int MaxConnectionsPerEndpoint,
    int MaxRetiringConnections)
{
    internal SharpLinkClusterOptions CreateOptions()
        => new()
        {
            MaxEndpoints = MaxEndpoints,
            MinReadyEndpoints = MinReadyEndpoints,
            MaxConnections = MaxConnections,
            MaxConnectionsPerEndpoint = MaxConnectionsPerEndpoint,
            MaxRetiringConnections = MaxRetiringConnections
        };
}

internal readonly record struct ClientRetryPlan(
    int MaxAttempts,
    TimeSpan InitialBackoff,
    TimeSpan MaxBackoff,
    double JitterRatio)
{
    internal SharpLinkRetryOptions CreateOptions()
        => new()
        {
            MaxAttempts = MaxAttempts,
            InitialBackoff = InitialBackoff,
            MaxBackoff = MaxBackoff,
            JitterRatio = JitterRatio
        };
}

internal readonly record struct ClientCircuitBreakerPlan(
    int MinimumThroughput,
    double FailureRatio,
    TimeSpan SamplingDuration,
    TimeSpan BreakDuration,
    int HalfOpenMaxCalls)
{
    internal SharpLinkCircuitBreakerOptions CreateOptions()
        => new()
        {
            MinimumThroughput = MinimumThroughput,
            FailureRatio = FailureRatio,
            SamplingDuration = SamplingDuration,
            BreakDuration = BreakDuration,
            HalfOpenMaxCalls = HalfOpenMaxCalls
        };
}
