namespace SharpLink.Server;

/// <summary>Configures transports, services, security, limits, and runtime behavior for a SharpLink server.</summary>
public class SharpLinkServerBuilder : ISharpLinkServerBuilder
{
    private const string ConsumedBuilderMessage = "This SharpLink builder has already been consumed.";

    private readonly object _configurationGate = new();
    private readonly SharpLinkRuntimeContextBuilder _runtimeContextBuilder = new();
    private readonly HashSet<Type> _enabledServices = [];
    private readonly HashSet<Type> _excludedServices = [];
    private readonly Dictionary<Type, ReplacementServiceDefinition> _replacementServices = [];
    private readonly List<ISharpLinkServerInterceptor> _interceptors = [];

    private BuilderState _state;
    private IServerTransportListener? _transport;
    private ServerRuntimeResources? _pendingResources;
    private TimeSpan _heartbeatCheckInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private RpcSessionFlushOptions? _rpcSessionFlushOptions;
    private bool _automaticServiceRegistration = true;
    private IServiceProvider? _serviceProvider;
    private ILoggerFactory? _loggerFactory;
    private ISharpLinkServerAuthenticator? _authenticator;
    private bool _authenticationRequired;
    private bool _allowUnencrypted;
    private bool _allowUnauthenticated;
    private IRpcExceptionMapper? _exceptionMapper;
    private bool _includeExceptionDetails;
    private SharpLinkAdmissionControlOptions? _admissionControlOptions;
    private SharpLinkConnectionAdmissionOptions? _connectionAdmissionOptions;

    /// <summary>Creates a server builder with safe runtime and heartbeat defaults.</summary>
    public static SharpLinkServerBuilder Create() => new();

    /// <summary>Gets the currently configured server transport listener.</summary>
    public IServerTransportListener? Transport
    {
        get
        {
            lock (_configurationGate)
                return _transport;
        }
    }

    /// <summary>Uses a server listener owned by the built server.</summary>
    public SharpLinkServerBuilder UseTransport(IServerTransportListener transport)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(transport);
            if (_transport is not null)
                throw new InvalidOperationException("A Server transport has already been configured for this builder.");
            _transport = transport;
        });
        return this;
    }

    /// <summary>Configures an instance-scoped server authenticator.</summary>
    public SharpLinkServerBuilder UseAuthenticator(ISharpLinkServerAuthenticator authenticator)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(authenticator);
            _authenticator = authenticator;
        });
        return this;
    }

    /// <summary>Requires authentication and fails <see cref="Build"/> when no authenticator is registered.</summary>
    public SharpLinkServerBuilder RequireAuthentication()
    {
        Configure(() => _authenticationRequired = true);
        return this;
    }

    /// <summary>Explicitly allows a non-loopback TCP listener to use plaintext instead of TLS.</summary>
    public SharpLinkServerBuilder AllowUnencrypted()
    {
        Configure(() => _allowUnencrypted = true);
        return this;
    }

    /// <summary>Explicitly allows a non-loopback TCP listener to run without required authentication.</summary>
    public SharpLinkServerBuilder AllowUnauthenticated()
    {
        Configure(() => _allowUnauthenticated = true);
        return this;
    }

    /// <summary>Changes a configured TCP listener to bind to <see cref="IPAddress.Any"/>.</summary>
    public SharpLinkServerBuilder ListenOnAnyAddress()
        => ListenOn(IPAddress.Any);

    /// <summary>Changes a configured TCP listener to bind to <see cref="IPAddress.Loopback"/>.</summary>
    public SharpLinkServerBuilder ListenOnLoopback()
        => ListenOn(IPAddress.Loopback);

    /// <summary>Changes a configured TCP listener to bind to the supplied address.</summary>
    public SharpLinkServerBuilder ListenOn(IPAddress address)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(address);
            if (_transport is not SocketServerTransportListener socket)
                throw new InvalidOperationException("A TCP transport must be configured before changing the listen address.");
            socket.ConfigureListenAddress(address);
        });
        return this;
    }

    /// <summary>Configures TLS for the currently configured TCP listener.</summary>
    public SharpLinkServerBuilder UseTls(
        SslServerAuthenticationOptions tlsOptions,
        TimeSpan? tlsHandshakeTimeout = null)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(tlsOptions);
            if (_transport is not SocketServerTransportListener socket)
                throw new InvalidOperationException("TLS can only be configured for a TCP transport.");
            socket.ConfigureTls(tlsOptions, tlsHandshakeTimeout);
        });
        return this;
    }

    /// <summary>Adds an interceptor to the initial server pipeline in registration order. After Build, use <see cref="ISharpLinkServer.ReplaceInterceptors"/> for runtime replacement.</summary>
    public SharpLinkServerBuilder AddInterceptor(ISharpLinkServerInterceptor interceptor)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(interceptor);
            _interceptors.Add(interceptor);
        });
        return this;
    }

    /// <summary>Configures an instance-scoped business exception mapper.</summary>
    public SharpLinkServerBuilder UseExceptionMapper(IRpcExceptionMapper exceptionMapper)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(exceptionMapper);
            _exceptionMapper = exceptionMapper;
        });
        return this;
    }

    /// <summary>Includes service exception messages in default Internal responses. Disabled by default.</summary>
    public SharpLinkServerBuilder EnableDetailedErrors(bool enabled = true)
    {
        Configure(() => _includeExceptionDetails = enabled);
        return this;
    }

    /// <summary>Sets a fallback codec resolver scoped to servers built by this builder.</summary>
    /// <param name="codecResolver">Returns a codec for a requested type, or <see langword="null"/> when unresolved.</param>
    public SharpLinkServerBuilder UseSerializer(Func<Type, IRpcCodec?>? codecResolver)
    {
        Configure(() => _runtimeContextBuilder.UseCodecResolver(codecResolver));
        return this;
    }

    /// <summary>Configures instance-scoped runtime behavior.</summary>
    public SharpLinkServerBuilder UseRuntime(Action<SharpLinkRuntimeOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            _runtimeContextBuilder.Configure(configure);
        });
        return this;
    }

    /// <summary>Uses an application-owned time source for the built server. The server never disposes it.</summary>
    public SharpLinkServerBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            _runtimeContextBuilder.UseTimeProvider(timeProvider);
        });
        return this;
    }

    /// <summary>
    /// Uses an isolated generated-manifest source for this Server build. The source is queried once
    /// by Compile and is not retained by the resulting Server.
    /// </summary>
    internal SharpLinkServerBuilder UseGeneratedManifestSource(IGeneratedManifestSource source)
    {
        Configure(() => _runtimeContextBuilder.UseGeneratedManifestSource(source));
        return this;
    }

    /// <summary>Enables bounded active admission control for calls accepted by this server.</summary>
    public SharpLinkServerBuilder UseAdmissionControl(Action<SharpLinkAdmissionControlOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            if (_admissionControlOptions is not null)
                throw new InvalidOperationException("Admission control has already been configured for this builder.");
            var options = new SharpLinkAdmissionControlOptions();
            configure(options);
            options.Validate();
            _admissionControlOptions = options;
        });
        return this;
    }

    /// <summary>
    /// Configures the pre-call connection resource bounds for this server: the maximum
    /// simultaneously live accepted connections and the maximum simultaneously handshaking
    /// connections. Over-limit connections are rejected (closed) immediately.
    /// </summary>
    public SharpLinkServerBuilder UseConnectionAdmission(Action<SharpLinkConnectionAdmissionOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            if (_connectionAdmissionOptions is not null)
                throw new InvalidOperationException("Connection admission has already been configured for this builder.");
            var options = new SharpLinkConnectionAdmissionOptions();
            configure(options);
            options.Validate();
            _connectionAdmissionOptions = options;
        });
        return this;
    }

    /// <summary>Uses the supplied application-owned logger factory.</summary>
    public SharpLinkServerBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            _loggerFactory = loggerFactory;
        });
        return this;
    }

    /// <summary>Configures the instance-owned outbound buffer pool.</summary>
    public SharpLinkServerBuilder UseBufferWriterPool(Action<BufferWriterPoolOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            _runtimeContextBuilder.ConfigureBufferPool(configure);
        });
        return this;
    }

    /// <summary>Configures striped state-store concurrency for this server.</summary>
    public SharpLinkServerBuilder UseStateStoreConcurrency(Action<RuntimeConcurrencyOptions> configure)
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

    /// <summary>Configures the heartbeat inspection interval and peer-inactivity timeout.</summary>
    public SharpLinkServerBuilder UseHeartbeat(TimeSpan checkInterval, TimeSpan timeout)
    {
        Configure(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(checkInterval, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            if (timeout <= checkInterval)
                throw new ArgumentException("Heartbeat timeout must be greater than check interval.");
            _heartbeatCheckInterval = checkInterval;
            _heartbeatTimeout = timeout;
        });
        return this;
    }

    /// <summary>Configures how often the server checks sessions for heartbeat timeout.</summary>
    public SharpLinkServerBuilder UseHeartbeatCheckInterval(TimeSpan checkInterval)
    {
        Configure(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(checkInterval, TimeSpan.Zero);
            if (_heartbeatTimeout <= checkInterval)
                throw new ArgumentException("Heartbeat timeout must be greater than check interval.");
            _heartbeatCheckInterval = checkInterval;
        });
        return this;
    }

    /// <summary>Configures how long peer inactivity is allowed before a session is closed.</summary>
    public SharpLinkServerBuilder UseHeartbeatTimeout(TimeSpan timeout)
    {
        Configure(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            if (timeout <= _heartbeatCheckInterval)
                throw new ArgumentException("Heartbeat timeout must be greater than check interval.");
            _heartbeatTimeout = timeout;
        });
        return this;
    }

    /// <summary>Enables bounded send coalescing by byte threshold and maximum latency.</summary>
    public SharpLinkServerBuilder UseRpcSessionFlush(int flushSizeThreshold, TimeSpan maxLatency)
    {
        Configure(() => _rpcSessionFlushOptions = RpcSessionFlushOptions.Create(flushSizeThreshold, maxLatency));
        return this;
    }

    /// <summary>Configures per-server protocol safety limits.</summary>
    public SharpLinkServerBuilder UseProtocol(Action<SharpLinkProtocolOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            _runtimeContextBuilder.Configure(options => configure(options.Protocol));
        });
        return this;
    }

    /// <summary>Uses an application-owned provider for service dependencies and per-call scopes.</summary>
    public SharpLinkServerBuilder UseServiceProvider(IServiceProvider serviceProvider)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            _serviceProvider = serviceProvider;
        });
        return this;
    }

    /// <summary>Disables automatic service exposure for this builder; explicitly enabled and replaced services remain.</summary>
    public SharpLinkServerBuilder DisableAutomaticServiceRegistration()
    {
        Configure(() => _automaticServiceRegistration = false);
        return this;
    }

    /// <summary>Requires and enables the generated service for a contract on this builder.</summary>
    public SharpLinkServerBuilder EnableService<TContract>()
        where TContract : class, IService
    {
        Configure(() =>
        {
            _excludedServices.Remove(typeof(TContract));
            _enabledServices.Add(typeof(TContract));
        });
        return this;
    }

    /// <summary>Excludes the generated service for a contract from this builder.</summary>
    public SharpLinkServerBuilder ExcludeService<TContract>()
        where TContract : class, IService
    {
        Configure(() =>
        {
            _enabledServices.Remove(typeof(TContract));
            _excludedServices.Add(typeof(TContract));
        });
        return this;
    }

    /// <summary>Replaces an automatically generated service with a caller-owned singleton instance.</summary>
    public SharpLinkServerBuilder ReplaceService<TContract>(TContract instance)
        where TContract : class, IService
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(instance);
            _replacementServices[typeof(TContract)] = new ReplacementServiceDefinition(
                instance,
                Factory: null,
                SharpLinkServiceLifetime.Singleton,
                CallerOwned: true);
        });
        return this;
    }

    /// <summary>Replaces an automatically generated service with a provider-aware factory.</summary>
    public SharpLinkServerBuilder ReplaceService<TContract>(
        Func<IServiceProvider, TContract> factory,
        SharpLinkServiceLifetime lifetime = SharpLinkServiceLifetime.Singleton)
        where TContract : class, IService
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(factory);
            ValidateLifetime(lifetime);
            _replacementServices[typeof(TContract)] = new ReplacementServiceDefinition(
                Instance: null,
                provider => factory(provider) ?? throw new InvalidOperationException(
                    $"Replacement factory for '{typeof(TContract).FullName}' returned null."),
                lifetime,
                CallerOwned: false);
        });
        return this;
    }

    /// <inheritdoc />
    public ISharpLinkServer Build() => Materialize(CompileForBuild());

    private ServerBuildPlan CompileForBuild()
    {
        BeginBuild();
        try
        {
            var plan = CompilePlan();
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

    private ServerBuildPlan CompilePlan()
    {
        var transport = _transport ?? throw new InvalidOperationException("Transport must be set before building the server.");
        if (_authenticationRequired && _authenticator is null)
            throw new InvalidOperationException("RequireAuthentication needs an ISharpLinkServerAuthenticator.");
        ValidateTransportSecurity(transport);

        var runtimeContext = _runtimeContextBuilder.Compile();
        var manifests = runtimeContext.GeneratedManifests;
        var services = CompileServicePlan(
            manifests,
            _automaticServiceRegistration,
            _enabledServices.ToFrozenSet(),
            _excludedServices.ToFrozenSet(),
            [.. _replacementServices]);

        return new ServerBuildPlan(
            new ServerRuntimeResources(transport),
            runtimeContext,
            services,
            _heartbeatCheckInterval,
            _heartbeatTimeout,
            _rpcSessionFlushOptions,
            _loggerFactory ?? NullLoggerFactory.Instance,
            _authenticator,
            _authenticationRequired,
            [.. _interceptors],
            _exceptionMapper ?? new DefaultRpcExceptionMapper(_includeExceptionDetails),
            _serviceProvider,
            _admissionControlOptions?.CloneValidated(),
            _connectionAdmissionOptions?.CloneValidated() ??
                new SharpLinkConnectionAdmissionOptions().CloneValidated());
    }

    private void ValidateTransportSecurity(IServerTransportListener transport)
    {
        if (transport is not SocketServerTransportListener socket ||
            socket.LocalEndPoint is not IPEndPoint ipEndPoint)
        {
            return;
        }

        if (!IsLoopback(ipEndPoint.Address))
        {
            if (!socket.UsesTls && !_allowUnencrypted)
            {
                throw new InvalidOperationException(
                    "A non-loopback TCP listener without TLS requires an explicit AllowUnencrypted() opt-in.");
            }

            if (!_authenticationRequired && !_allowUnauthenticated)
            {
                throw new InvalidOperationException(
                    "A non-loopback TCP listener without required authentication requires an explicit AllowUnauthenticated() opt-in.");
            }
        }
    }

    private static bool IsLoopback(IPAddress address)
    {
        if (address.Equals(IPAddress.Loopback) ||
            address.Equals(IPAddress.IPv6Loopback))
        {
            return true;
        }

        var ipv4 = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return ipv4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
               ipv4.GetAddressBytes()[0] == 127;
    }

    private ISharpLinkServer Materialize(ServerBuildPlan plan)
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
                SynchronousBuildResourceMetadata.FrameworkOwned("Server runtime context"));
            if (plan.Resources.Transport is IPerformanceProfileAwareTransport profileAwareTransport)
                profileAwareTransport.BindPerformanceProfile(runtimeContext.PerformanceProfile);

            var serviceProvider = plan.CallerServiceProvider;
            IAsyncDisposable? ownedServiceProvider = null;
            if (serviceProvider is null)
            {
                var internalProvider = new ServiceCollection().BuildServiceProvider(
                    new ServiceProviderOptions { ValidateScopes = true });
                ownedServiceProvider = transaction.Own(
                    (IAsyncDisposable)internalProvider,
                    static provider => SharpLinkAsyncCleanup.DisposeSynchronously(provider),
                    SynchronousBuildResourceMetadata.FrameworkOwned("Server framework service provider"));
                serviceProvider = internalProvider;
            }
            else
            {
                transaction.Own(
                    serviceProvider,
                    cleanup: null,
                    metadata: SynchronousBuildResourceMetadata.CallerOwned("Server caller service provider"));
            }

            SharpLinkAdmissionController? admissionController = null;
            var staticManifests = plan.CreateStaticManifestSnapshot();
            if (plan.AdmissionControlOptions is not null)
            {
                admissionController = transaction.Own(
                    SharpLinkAdmissionController.Create(
                        plan.AdmissionControlOptions,
                        staticManifests,
                        runtimeContext.TimeProvider),
                    static controller => SharpLinkAsyncCleanup.DisposeSynchronously(controller),
                    SynchronousBuildResourceMetadata.FrameworkOwned("Server admission controller"));
            }

            var registrationsByContract = new Dictionary<long, ServiceRegistration>(plan.ServiceCount);
            for (var index = 0; index < plan.ServiceCount; index++)
            {
                var entry = plan.GetService(index);
                entry.Registration.ValidateDependencies(serviceProvider);
                var registration = transaction.Own(
                    entry.Registration.Materialize(
                        RpcGeneratedCodecResolver.GetProvider(runtimeContext, entry.Registration.ContractType))
                        .Build(serviceProvider),
                    static value => SharpLinkAsyncCleanup.DisposeSynchronously(value),
                    SynchronousBuildResourceMetadata.FrameworkOwned("Server service registration"));
                registrationsByContract.Add(entry.ContractId, registration);
            }

            var services = registrationsByContract.ToFrozenDictionary();
            var logger = plan.LoggerFactory.CreateLogger<SharpLinkServer>();
            var connectionAdmission = new ServerConnectionAdmission(
                plan.ConnectionAdmissionOptions.MaxConcurrentConnections,
                plan.ConnectionAdmissionOptions.MaxConcurrentHandshakes);
            var composition = new ServerRuntimeComposition(
                plan.Resources.Transport,
                services,
                plan.HeartbeatCheckInterval,
                plan.HeartbeatTimeout,
                logger,
                runtimeContext,
                plan.Authenticator,
                plan.AuthenticationRequired,
                runtimeContext.Protocol.CloneValidated(),
                plan.RpcSessionFlushOptions,
                plan.CreateInterceptorSnapshot(),
                plan.ExceptionMapper,
                new ServerServiceCleanup(services.Values, ownedServiceProvider),
                serviceProvider,
                staticManifests,
                admissionController,
                connectionAdmission,
                ServerShutdownPlan.Default,
                SharpLinkServer.CreateFrameworkTaskSupervisor(logger));
            var server = new SharpLinkServer(composition);
            transaction.Commit();
            plan.Resources.MarkTransferred();
            CompleteBuild();
            return server;
        }
        catch (Exception buildException)
        {
            if (materializationStarted)
                plan.Resources.MarkRolledBack();
            CompleteBuild();
            if (materializationStarted)
            {
                transaction.Rollback(buildException);
                throw new System.Diagnostics.UnreachableException();
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(buildException).Throw();
            throw new System.Diagnostics.UnreachableException();
        }
    }

    private static ServerServiceRegistrationPlanEntry[] CompileServicePlan(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests,
        bool automaticServiceRegistration,
        FrozenSet<Type> enabledServices,
        FrozenSet<Type> excludedServices,
        IReadOnlyList<KeyValuePair<Type, ReplacementServiceDefinition>> replacementServices)
    {
        var replacementTypes = new HashSet<Type>(replacementServices.Count);
        for (var replacementIndex = 0; replacementIndex < replacementServices.Count; replacementIndex++)
            replacementTypes.Add(replacementServices[replacementIndex].Key);
        var contracts = new Dictionary<long, (SharpLinkGeneratedContractDescriptor Descriptor, ISharpLinkGeneratedAssemblyManifest Manifest)>();
        var services = new Dictionary<long, (SharpLinkGeneratedServiceDescriptor Descriptor, ISharpLinkGeneratedAssemblyManifest Manifest)>();
        for (var manifestIndex = 0; manifestIndex < manifests.Count; manifestIndex++)
        {
            var manifest = manifests[manifestIndex];
            ValidateManifest(manifest);
            for (var index = 0; index < manifest.Contracts.Count; index++)
            {
                var incoming = manifest.Contracts[index];
                if (contracts.TryGetValue(incoming.ContractId, out var existing))
                    throw CreateBuildConflict("Contract", incoming, manifest, existing.Descriptor, existing.Manifest);
                contracts.Add(incoming.ContractId, (incoming, manifest));
            }
            for (var index = 0; index < manifest.Services.Count; index++)
            {
                var incoming = manifest.Services[index];
                if (services.TryGetValue(incoming.ContractId, out var existing) &&
                    !replacementTypes.Contains(incoming.ContractType))
                {
                    throw CreateBuildConflict("Service", incoming, manifest, existing.Descriptor, existing.Manifest);
                }
                services.TryAdd(incoming.ContractId, (incoming, manifest));
            }
        }

        var definitions = new List<ServerServiceRegistrationPlanEntry>(services.Count);
        var foundEnabled = new HashSet<Type>();
        foreach (var pair in services)
        {
            var service = pair.Value.Descriptor;
            if (replacementTypes.Contains(service.ContractType))
                continue;
            var explicitlyEnabled = enabledServices.Contains(service.ContractType);
            if (explicitlyEnabled)
                foundEnabled.Add(service.ContractType);
            if ((!automaticServiceRegistration && !explicitlyEnabled) || excludedServices.Contains(service.ContractType))
                continue;
            if (!contracts.TryGetValue(service.ContractId, out var contract) ||
                !ReferenceEquals(contract.Descriptor.ContractType, service.ContractType))
            {
                throw new InvalidOperationException(
                    $"Generated service '{service.ImplementationName}' requires contract '{service.ContractName}' " +
                    $"({service.ContractId}), but its contract-owned manifest is not loaded.");
            }
            definitions.Add(new ServerServiceRegistrationPlanEntry(
                service.ContractId,
                new ServerServiceRegistrationPlan(
                    service.ContractType,
                    service.ImplementationName,
                    contract.Descriptor.StubFactory,
                    service.Lifetime,
                    service.Activator,
                    instance: null,
                    callerOwned: false,
                    service.Dependencies)));
        }

        for (var replacementIndex = 0; replacementIndex < replacementServices.Count; replacementIndex++)
        {
            var replacement = replacementServices[replacementIndex];
            var contract = contracts.Values.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Descriptor.ContractType, replacement.Key));
            if (contract.Descriptor is null)
            {
                throw new InvalidOperationException(
                    $"Generated contract '{replacement.Key.FullName}' required by ReplaceService was not found.");
            }
            var value = replacement.Value;
            definitions.Add(new ServerServiceRegistrationPlanEntry(
                contract.Descriptor.ContractId,
                new ServerServiceRegistrationPlan(
                    replacement.Key,
                    replacement.Key.FullName ?? replacement.Key.Name,
                    contract.Descriptor.StubFactory,
                    value.Lifetime,
                    value.Factory,
                    value.Instance,
                    value.CallerOwned,
                    [])));
            foundEnabled.Add(replacement.Key);
        }

        foreach (var required in enabledServices)
        {
            if (!foundEnabled.Contains(required))
            {
                throw new InvalidOperationException(
                    $"Generated service for required contract '{required.FullName}' was not found.");
            }
        }
        return [.. definitions];
    }

    private void DisposeUnbuiltResources()
    {
        ServerRuntimeResources? resources;
        lock (_configurationGate)
        {
            if (_state == BuilderState.Consumed)
                return;

            resources = _pendingResources ?? (_transport is null ? null : new ServerRuntimeResources(_transport));
            _pendingResources = resources;
            _transport = null;
            _state = BuilderState.Consumed;
        }

        resources?.DisposeUnmaterialized();
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
            _transport = null;
            _pendingResources = null;
            _state = BuilderState.Consumed;
        }
    }

    private void EnsureMutable()
    {
        if (_state != BuilderState.Mutable)
            throw new InvalidOperationException(ConsumedBuilderMessage);
    }

    private static void ValidateManifest(ISharpLinkGeneratedAssemblyManifest manifest)
        => SharpLinkGeneratedManifestCompatibility.ThrowIfIncompatible(manifest);

    private static InvalidOperationException CreateBuildConflict(
        string kind,
        SharpLinkGeneratedContractDescriptor incoming,
        ISharpLinkGeneratedAssemblyManifest incomingManifest,
        SharpLinkGeneratedContractDescriptor existing,
        ISharpLinkGeneratedAssemblyManifest existingManifest)
        => new(
            $"{kind} conflict for '{incoming.ContractName}' ({incoming.ContractId}). " +
            $"Incoming Assembly='{incomingManifest.OwnerAssembly.FullName}', " +
            $"ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(incomingManifest.OwnerAssembly)}', " +
            $"Fingerprint='{incoming.Fingerprint}'; Existing Assembly='{existingManifest.OwnerAssembly.FullName}', " +
            $"ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(existingManifest.OwnerAssembly)}', " +
            $"Fingerprint='{existing.Fingerprint}'.");

    private static InvalidOperationException CreateBuildConflict(
        string kind,
        SharpLinkGeneratedServiceDescriptor incoming,
        ISharpLinkGeneratedAssemblyManifest incomingManifest,
        SharpLinkGeneratedServiceDescriptor existing,
        ISharpLinkGeneratedAssemblyManifest existingManifest)
        => new(
            $"{kind} conflict for '{incoming.ContractName}' ({incoming.ContractId}). " +
            $"Incoming Service='{incoming.ImplementationName}', Assembly='{incomingManifest.OwnerAssembly.FullName}', " +
            $"ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(incomingManifest.OwnerAssembly)}', " +
            $"Fingerprint='{incoming.Fingerprint}'; Existing Service='{existing.ImplementationName}', " +
            $"Assembly='{existingManifest.OwnerAssembly.FullName}', " +
            $"ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(existingManifest.OwnerAssembly)}', " +
            $"Fingerprint='{existing.Fingerprint}'.");

    private static void ValidateLifetime(SharpLinkServiceLifetime lifetime)
    {
        if (lifetime is not SharpLinkServiceLifetime.Singleton and
            not SharpLinkServiceLifetime.Connection and
            not SharpLinkServiceLifetime.Call)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }
    }

    private enum BuilderState : byte
    {
        Mutable,
        Building,
        Consumed
    }
}
