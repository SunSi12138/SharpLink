namespace SharpLink.Server;

/// <summary>
/// The sole construction input for <see cref="SharpLinkServer"/>. All values are materialized from
/// one immutable <see cref="ServerBuildPlan"/> and owned by the existing build transaction before
/// this composition reaches the runtime object.
/// </summary>
internal sealed class ServerRuntimeComposition
{
    private readonly ISharpLinkServerInterceptor[] _interceptors;
    private readonly ISharpLinkGeneratedAssemblyManifest[] _staticManifests;

    internal ServerRuntimeComposition(
        IServerTransportListener transportListener,
        FrozenDictionary<long, ServiceRegistration> services,
        TimeSpan heartbeatCheckInterval,
        TimeSpan heartbeatTimeout,
        ILogger logger,
        SharpLinkRuntimeContext runtimeContext,
        ISharpLinkServerAuthenticator? authenticator,
        bool authenticationRequired,
        SharpLinkProtocolOptions protocolOptions,
        RpcSessionFlushOptions? rpcSessionFlushOptions,
        ISharpLinkServerInterceptor[] interceptors,
        IRpcExceptionMapper exceptionMapper,
        ServerServiceCleanup serviceCleanup,
        IServiceProvider serviceProvider,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> staticManifests,
        SharpLinkAdmissionController? admissionController,
        ServerShutdownPlan shutdownPlan,
        FrameworkTaskSupervisor frameworkTasks)
    {
        TransportListener = transportListener ?? throw new ArgumentNullException(nameof(transportListener));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatCheckInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatTimeout, TimeSpan.Zero);
        if (heartbeatTimeout <= heartbeatCheckInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than check interval.");
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        RuntimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        ProtocolOptions = protocolOptions ?? throw new ArgumentNullException(nameof(protocolOptions));
        ArgumentNullException.ThrowIfNull(interceptors);
        ExceptionMapper = exceptionMapper ?? throw new ArgumentNullException(nameof(exceptionMapper));
        ServiceCleanup = serviceCleanup ?? throw new ArgumentNullException(nameof(serviceCleanup));
        ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        ArgumentNullException.ThrowIfNull(staticManifests);
        ShutdownPlan = shutdownPlan ?? throw new ArgumentNullException(nameof(shutdownPlan));
        FrameworkTasks = frameworkTasks ?? throw new ArgumentNullException(nameof(frameworkTasks));

        _interceptors = [.. interceptors];
        _staticManifests = new ISharpLinkGeneratedAssemblyManifest[staticManifests.Count];
        for (var index = 0; index < _staticManifests.Length; index++)
            _staticManifests[index] = staticManifests[index] ?? throw new ArgumentException("Static manifests cannot contain null.", nameof(staticManifests));
        HeartbeatCheckInterval = heartbeatCheckInterval;
        HeartbeatTimeout = heartbeatTimeout;
        Authenticator = authenticator;
        AuthenticationRequired = authenticationRequired;
        RpcSessionFlushOptions = rpcSessionFlushOptions;
        AdmissionController = admissionController;
    }

    internal IServerTransportListener TransportListener { get; }

    internal FrozenDictionary<long, ServiceRegistration> Services { get; }

    internal TimeSpan HeartbeatCheckInterval { get; }

    internal TimeSpan HeartbeatTimeout { get; }

    internal ILogger Logger { get; }

    internal SharpLinkRuntimeContext RuntimeContext { get; }

    internal ISharpLinkServerAuthenticator? Authenticator { get; }

    internal bool AuthenticationRequired { get; }

    internal SharpLinkProtocolOptions ProtocolOptions { get; }

    internal RpcSessionFlushOptions? RpcSessionFlushOptions { get; }

    internal ISharpLinkServerInterceptor[] Interceptors => _interceptors;

    internal IRpcExceptionMapper ExceptionMapper { get; }

    internal ServerServiceCleanup ServiceCleanup { get; }

    internal IServiceProvider ServiceProvider { get; }

    internal IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> StaticManifests => _staticManifests;

    internal SharpLinkAdmissionController? AdmissionController { get; }

    internal ServerShutdownPlan ShutdownPlan { get; }

    internal FrameworkTaskSupervisor FrameworkTasks { get; }
}
