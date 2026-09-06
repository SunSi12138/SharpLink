namespace SharpLink.Server;

/// <summary>One frozen replacement registration selected during Server plan compilation.</summary>
internal sealed record ReplacementServiceDefinition(
    object? Instance,
    Func<IServiceProvider, object>? Factory,
    SharpLinkServiceLifetime Lifetime,
    bool CallerOwned);

/// <summary>
/// Holds the listener whose ownership transfers to the completed Server. It is the only pre-existing
/// framework-owned Server resource; service providers, admission controllers, and registrations are
/// materialized later and registered with the same build transaction.
/// </summary>
internal sealed class ServerRuntimeResources
{
    private int _state;

    internal ServerRuntimeResources(IServerTransportListener transport)
        => Transport = transport ?? throw new ArgumentNullException(nameof(transport));

    /// <summary>Framework-owned by the completed Server; rollback disposes this listener.</summary>
    internal IServerTransportListener Transport { get; }

    internal void RegisterWith(SynchronousBuildTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        EnsurePending();
        transaction.Own(
            Transport,
            static listener => SharpLinkAsyncCleanup.DisposeSynchronously(listener),
            SynchronousBuildResourceMetadata.FrameworkOwned("Server transport listener"));
    }

    internal void MarkTransferred()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("Server runtime resources have already reached a terminal state.");
    }

    internal void MarkRolledBack()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
            throw new InvalidOperationException("Server runtime resources have already reached a terminal state.");
    }

    internal void DisposeUnmaterialized()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
            return;

        using var transaction = new SynchronousBuildTransaction();
        transaction.Own(
            Transport,
            static listener => SharpLinkAsyncCleanup.DisposeSynchronously(listener),
            SynchronousBuildResourceMetadata.FrameworkOwned("unbuilt Server transport listener"));
        transaction.Rollback();
    }

    private void EnsurePending()
    {
        if (Volatile.Read(ref _state) != 0)
            throw new InvalidOperationException("Server runtime resources have already reached a terminal state.");
    }
}

/// <summary>Immutable description of one generated Server registration before runtime codecs materialize.</summary>
internal sealed class ServerServiceRegistrationPlan
{
    private readonly Type[] _dependencies;

    internal ServerServiceRegistrationPlan(
        Type contractType,
        string implementationName,
        Func<IRpcCodecProvider, IRpcStub> stubFactory,
        SharpLinkServiceLifetime lifetime,
        Func<IServiceProvider, object>? factory,
        object? instance,
        bool callerOwned,
        IReadOnlyList<Type> dependencies)
    {
        ContractType = contractType ?? throw new ArgumentNullException(nameof(contractType));
        ImplementationName = implementationName ?? throw new ArgumentNullException(nameof(implementationName));
        StubFactory = stubFactory ?? throw new ArgumentNullException(nameof(stubFactory));
        Lifetime = lifetime;
        Factory = factory;
        Instance = instance;
        CallerOwned = callerOwned;
        ArgumentNullException.ThrowIfNull(dependencies);
        _dependencies = new Type[dependencies.Count];
        for (var index = 0; index < _dependencies.Length; index++)
            _dependencies[index] = dependencies[index] ?? throw new ArgumentException("Service dependencies cannot contain null.", nameof(dependencies));
    }

    internal Type ContractType { get; }
    internal string ImplementationName { get; }
    internal Func<IRpcCodecProvider, IRpcStub> StubFactory { get; }
    internal SharpLinkServiceLifetime Lifetime { get; }
    internal Func<IServiceProvider, object>? Factory { get; }
    internal object? Instance { get; }
    internal bool CallerOwned { get; }

    internal ServiceRegistrationDefinition Materialize(IRpcCodecProvider codecs)
        => new(
            ContractType,
            StubFactory(codecs ?? throw new ArgumentNullException(nameof(codecs))),
            Lifetime,
            Factory,
            Instance,
            CallerOwned);

    internal void ValidateDependencies(IServiceProvider provider)
    {
        if (_dependencies.Length == 0)
            return;
        var availability = provider.GetService<IServiceProviderIsService>();
        if (availability is null)
            return;
        for (var index = 0; index < _dependencies.Length; index++)
        {
            var dependency = _dependencies[index];
            if (!availability.IsService(dependency))
            {
                throw new InvalidOperationException(
                    $"Required dependency '{dependency.FullName}' for generated RPC service " +
                    $"'{ImplementationName}' is not registered.");
            }
        }
    }
}

internal readonly record struct ServerServiceRegistrationPlanEntry(
    long ContractId,
    ServerServiceRegistrationPlan Registration);

/// <summary>Immutable Server build input. It has no disposal behavior and is materialized once.</summary>
internal sealed class ServerBuildPlan
{
    private readonly ISharpLinkServerInterceptor[] _interceptors;
    private readonly ServerServiceRegistrationPlanEntry[] _services;
    private int _materializationState;

    internal ServerBuildPlan(
        ServerRuntimeResources resources,
        SharpLinkRuntimeContextBuildPlan runtimeContext,
        ServerServiceRegistrationPlanEntry[] services,
        SharpLinkCompressionSendPolicy responseCompressionPolicy,
        TimeSpan heartbeatCheckInterval,
        TimeSpan heartbeatTimeout,
        RpcSessionFlushOptions? rpcSessionFlushOptions,
        ILoggerFactory loggerFactory,
        ISharpLinkServerAuthenticator? authenticator,
        bool authenticationRequired,
        ISharpLinkServerInterceptor[] interceptors,
        IRpcExceptionMapper exceptionMapper,
        IServiceProvider? callerServiceProvider,
        SharpLinkAdmissionControlOptions? admissionControlOptions,
        SharpLinkConnectionAdmissionOptions connectionAdmissionOptions)
    {
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        RuntimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        ArgumentNullException.ThrowIfNull(services);
        _services = [.. services];
        ResponseCompressionPolicy = responseCompressionPolicy ?? throw new ArgumentNullException(nameof(responseCompressionPolicy));
        _ = CompressionSendPolicySnapshot.CreateValidated(ResponseCompressionPolicy);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatCheckInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatTimeout, TimeSpan.Zero);
        if (heartbeatTimeout <= heartbeatCheckInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than check interval.");
        HeartbeatCheckInterval = heartbeatCheckInterval;
        HeartbeatTimeout = heartbeatTimeout;
        RpcSessionFlushOptions = rpcSessionFlushOptions;
        LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        Authenticator = authenticator;
        AuthenticationRequired = authenticationRequired;
        _interceptors = interceptors is { Length: > 0 } ? [.. interceptors] : [];
        ExceptionMapper = exceptionMapper ?? throw new ArgumentNullException(nameof(exceptionMapper));
        CallerServiceProvider = callerServiceProvider;
        AdmissionControlOptions = admissionControlOptions;
        ConnectionAdmissionOptions = connectionAdmissionOptions ?? throw new ArgumentNullException(nameof(connectionAdmissionOptions));
    }

    internal ServerRuntimeResources Resources { get; }
    internal SharpLinkRuntimeContextBuildPlan RuntimeContext { get; }
    internal int ServiceCount => _services.Length;
    internal SharpLinkCompressionSendPolicy ResponseCompressionPolicy { get; }
    internal ServerServiceRegistrationPlanEntry GetService(int index) => _services[index];
    internal TimeSpan HeartbeatCheckInterval { get; }
    internal TimeSpan HeartbeatTimeout { get; }
    internal RpcSessionFlushOptions? RpcSessionFlushOptions { get; }
    internal ILoggerFactory LoggerFactory { get; }
    internal ISharpLinkServerAuthenticator? Authenticator { get; }
    internal bool AuthenticationRequired { get; }
    internal IRpcExceptionMapper ExceptionMapper { get; }
    /// <summary>Caller-owned; it is registered with no cleanup and never disposed by SharpLink.</summary>
    internal IServiceProvider? CallerServiceProvider { get; }
    internal SharpLinkAdmissionControlOptions? AdmissionControlOptions { get; }

    internal SharpLinkConnectionAdmissionOptions ConnectionAdmissionOptions { get; }

    internal ISharpLinkServerInterceptor[] CreateInterceptorSnapshot()
        => _interceptors.Length == 0 ? [] : [.. _interceptors];

    internal IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CreateStaticManifestSnapshot()
        => RuntimeContext.GeneratedManifests;

    internal void BeginMaterialization()
    {
        if (Interlocked.CompareExchange(ref _materializationState, 1, 0) != 0)
            throw new InvalidOperationException("This Server build plan has already been materialized.");
    }
}
