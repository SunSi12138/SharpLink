namespace SharpLink.Client;

/// <summary>
/// Materializes one immutable <see cref="ClientBuildPlan"/> into a completed Client while owning
/// the synchronous construction transaction and every build-time resource acquired after Compile.
/// </summary>
internal static class ClientRuntimeMaterializer
{
    internal static ISharpLinkClient Materialize(ClientBuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
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
            return client;
        }
        catch (Exception buildException)
        {
            if (materializationStarted)
                plan.Resources.MarkRolledBack();
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
            plan.RequestCompressionPolicy,
            plan.BeforeReadyPublicationTestHook,
            staticManifests,
            SharpLinkClient.BuildStaticProxySnapshot(staticManifests, runtimeContext),
            plan.HeartbeatInterval,
            plan.HeartbeatTimeout,
            requestTimeout.HasValue,
            requestTimeout.GetValueOrDefault(),
            plan.RequestTimeoutSource,
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
}
