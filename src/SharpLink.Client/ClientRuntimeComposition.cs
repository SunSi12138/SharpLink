namespace SharpLink.Client;

/// <summary>
/// Explicit, already-materialized topology input for a completed Client. The builder selects one
/// subtype before the runtime object is constructed; the Client never infers a topology from nullable
/// constructor arguments.
/// </summary>
internal abstract class ClientRuntimeTopologyComposition
{
}

/// <summary>Represents the direct-transport (including one static endpoint) Client fast path.</summary>
internal sealed class FixedClientRuntimeTopologyComposition(
    SharpLinkEndpoint? endpoint) : ClientRuntimeTopologyComposition
{
    internal SharpLinkEndpoint? Endpoint { get; } = endpoint;
}

/// <summary>Represents an already-created static endpoint transport topology.</summary>
internal sealed class StaticClientRuntimeTopologyComposition : ClientRuntimeTopologyComposition
{
    private readonly StaticClientRuntimeEndpointState[] _endpointStates;

    internal StaticClientRuntimeTopologyComposition(
        StaticEndpointConfiguration[] configurations,
        SharpLinkClusterOptions clusterOptions,
        SharpLinkLoadBalancingStrategy loadBalancingStrategy,
        ISharpLinkEndpointSelector? endpointSelector)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        if (configurations.Length < 2)
            throw new ArgumentException("A static Client runtime topology requires two or more endpoint configurations.", nameof(configurations));
        for (var index = 0; index < configurations.Length; index++)
            ArgumentNullException.ThrowIfNull(configurations[index]);

        ClusterOptions = clusterOptions ?? throw new ArgumentNullException(nameof(clusterOptions));
        LoadBalancingStrategy = loadBalancingStrategy;
        EndpointSelector = endpointSelector;
        StaticEndpointConfiguration[] configurationSnapshot = [.. configurations];
        _endpointStates = new StaticClientRuntimeEndpointState[configurationSnapshot.Length];
        for (var index = 0; index < _endpointStates.Length; index++)
            _endpointStates[index] = new StaticClientRuntimeEndpointState(configurationSnapshot[index], index);
    }

    internal SharpLinkClusterOptions ClusterOptions { get; }

    internal SharpLinkLoadBalancingStrategy LoadBalancingStrategy { get; }

    internal ISharpLinkEndpointSelector? EndpointSelector { get; }

    // The composition owns these prebuilt states until it transfers them to the completed Client.
    // They are internal-only and the cluster never changes the configuration array itself.
    internal StaticClientRuntimeEndpointState[] EndpointStates => _endpointStates;
}

/// <summary>
/// Prebuilt mutable state for one static endpoint. It is created while the Builder materializes the
/// typed composition so the Client constructor neither enumerates endpoint configuration nor clones
/// it after construction has started.
/// </summary>
internal sealed class StaticClientRuntimeEndpointState
{
    private readonly Func<int> _readyConnectionCountProvider;
    private readonly Func<int> _activeCallCountProvider;
    private ClientConnection[] _readyConnections = [];

    internal StaticClientRuntimeEndpointState(StaticEndpointConfiguration configuration, int index)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Index = index;
        _readyConnectionCountProvider = GetReadyConnectionCount;
        _activeCallCountProvider = GetActiveCallCount;
    }

    public StaticEndpointConfiguration Configuration { get; }

    internal int Index { get; }

    internal HashSet<ClientConnection> Connections { get; } = [];

    public ClientConnection[] ReadyConnections => Volatile.Read(ref _readyConnections);

    internal Func<int> ReadyConnectionCountProvider => _readyConnectionCountProvider;

    internal Func<int> ActiveCallCountProvider => _activeCallCountProvider;

    internal int ConnectingCount { get; set; }

    internal int ReconnectDelayMilliseconds { get; set; } = 100;

    public Task? ReconnectTask { get; set; }

    internal Task? ExpansionTask { get; set; }

    internal int NonRetiringConnectionCount
    {
        get
        {
            var count = 0;
            foreach (var connection in Connections)
                if (connection.State == ClientConnectionState.Ready)
                    count++;
            return count;
        }
    }

    internal int ActiveCallCount => GetActiveCallCount();

    internal void PublishReadyConnections()
    {
        var ready = new List<ClientConnection>(Connections.Count);
        foreach (var connection in Connections)
            if (connection.CanAcceptCalls)
                ready.Add(connection);
        Volatile.Write(ref _readyConnections, ready.ToArray());
    }

    private int GetReadyConnectionCount() => ReadyConnections.Length;

    private int GetActiveCallCount()
    {
        var connections = ReadyConnections;
        var count = 0;
        for (var index = 0; index < connections.Length; index++)
            count += connections[index].ActiveCallCount;
        return count;
    }
}

/// <summary>Represents an already-bound resolver-backed Client topology.</summary>
internal sealed class DynamicClientRuntimeTopologyComposition(
    ISharpLinkEndpointResolver resolver,
    SharpLinkEndpointTransportFactory transportFactory,
    SharpLinkClusterOptions clusterOptions,
    SharpLinkLoadBalancingStrategy loadBalancingStrategy,
    ISharpLinkEndpointSelector? endpointSelector) : ClientRuntimeTopologyComposition
{
    internal ISharpLinkEndpointResolver Resolver { get; } = resolver ?? throw new ArgumentNullException(nameof(resolver));

    internal SharpLinkEndpointTransportFactory TransportFactory { get; } = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));

    internal SharpLinkClusterOptions ClusterOptions { get; } = clusterOptions ?? throw new ArgumentNullException(nameof(clusterOptions));

    internal SharpLinkLoadBalancingStrategy LoadBalancingStrategy { get; } = loadBalancingStrategy;

    internal ISharpLinkEndpointSelector? EndpointSelector { get; } = endpointSelector;

}

/// <summary>
/// The sole construction input for <see cref="SharpLinkClient"/>. Values are produced from one
/// immutable <see cref="ClientBuildPlan"/> during builder materialization; this type deliberately
/// contains no catalog lookup, mutable-builder reference, option fallback, or resource factory.
/// </summary>
internal sealed class ClientRuntimeComposition
{
    private readonly ISharpLinkGeneratedAssemblyManifest[] _staticManifests;
    private readonly ISharpLinkClientInterceptor[] _interceptors;

    internal ClientRuntimeComposition(
        IClientTransportFactory transportFactory,
        ClientRuntimeTopologyComposition topology,
        SharpLinkRuntimeContext runtimeContext,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> staticManifests,
        FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> staticProxies,
        TimeSpan heartbeatInterval,
        TimeSpan heartbeatTimeout,
        bool hasRequestTimeout,
        TimeSpan requestTimeout,
        ISharpLinkClientAuthenticator? authenticator,
        SharpLinkProtocolOptions protocolOptions,
        RpcSessionFlushOptions? rpcSessionFlushOptions,
        SharpLinkConnectionPoolOptions connectionPoolOptions,
        ISharpLinkClientInterceptor[] interceptors,
        SharpLinkRetryOptions? retryOptions,
        ISharpLinkRetryPolicy? retryPolicy,
        ISharpLinkEndpointAdmissionPolicy? endpointAdmissionPolicy,
        ISharpLinkReconnectJitter reconnectJitter,
        ILogger logger,
        FrameworkTaskSupervisor frameworkTasks)
    {
        TransportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        Topology = topology ?? throw new ArgumentNullException(nameof(topology));
        RuntimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        ArgumentNullException.ThrowIfNull(staticManifests);
        ArgumentNullException.ThrowIfNull(staticProxies);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatTimeout, TimeSpan.Zero);
        if (heartbeatTimeout <= heartbeatInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than interval.");
        if (hasRequestTimeout)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(protocolOptions);
        ArgumentNullException.ThrowIfNull(connectionPoolOptions);
        ArgumentNullException.ThrowIfNull(interceptors);

        _staticManifests = new ISharpLinkGeneratedAssemblyManifest[staticManifests.Count];
        for (var index = 0; index < _staticManifests.Length; index++)
            _staticManifests[index] = staticManifests[index] ?? throw new ArgumentException("Static manifests cannot contain null.", nameof(staticManifests));
        _interceptors = [.. interceptors];
        StaticProxies = staticProxies;
        HeartbeatInterval = heartbeatInterval;
        HeartbeatTimeout = heartbeatTimeout;
        HasRequestTimeout = hasRequestTimeout;
        RequestTimeout = requestTimeout;
        Authenticator = authenticator;
        ProtocolOptions = protocolOptions;
        RpcSessionFlushOptions = rpcSessionFlushOptions;
        ConnectionPoolOptions = connectionPoolOptions;
        RetryOptions = retryOptions;
        RetryPolicy = retryPolicy;
        EndpointAdmissionPolicy = endpointAdmissionPolicy;
        ReconnectJitter = reconnectJitter ?? throw new ArgumentNullException(nameof(reconnectJitter));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        FrameworkTasks = frameworkTasks ?? throw new ArgumentNullException(nameof(frameworkTasks));
    }

    internal IClientTransportFactory TransportFactory { get; }

    internal ClientRuntimeTopologyComposition Topology { get; }

    internal SharpLinkRuntimeContext RuntimeContext { get; }

    internal FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> StaticProxies { get; }

    internal TimeSpan HeartbeatInterval { get; }

    internal TimeSpan HeartbeatTimeout { get; }

    internal bool HasRequestTimeout { get; }

    internal TimeSpan RequestTimeout { get; }

    internal ISharpLinkClientAuthenticator? Authenticator { get; }

    internal SharpLinkProtocolOptions ProtocolOptions { get; }

    internal RpcSessionFlushOptions? RpcSessionFlushOptions { get; }

    internal SharpLinkConnectionPoolOptions ConnectionPoolOptions { get; }

    internal SharpLinkRetryOptions? RetryOptions { get; }

    internal ISharpLinkRetryPolicy? RetryPolicy { get; }

    internal ISharpLinkEndpointAdmissionPolicy? EndpointAdmissionPolicy { get; }

    internal ISharpLinkReconnectJitter ReconnectJitter { get; }

    internal ILogger Logger { get; }

    internal FrameworkTaskSupervisor FrameworkTasks { get; }

    internal IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> StaticManifests => _staticManifests;

    internal ISharpLinkClientInterceptor[] Interceptors => _interceptors;
}
