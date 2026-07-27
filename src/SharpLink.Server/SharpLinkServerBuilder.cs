namespace SharpLink.Server;

public class SharpLinkServerBuilder : ISharpLinkServerBuilder
{
    public static SharpLinkServerBuilder Create() => new();

    private IServerTransportListener? _transport;
    public IServerTransportListener? Transport=>_transport;
    private readonly SharpLinkRuntimeContextBuilder _runtimeContextBuilder = new();
    private TimeSpan _heartbeatCheckInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private RpcSessionFlushOptions? _rpcSessionFlushOptions;
    private bool _automaticServiceRegistration = true;
    private readonly HashSet<Type> _enabledServices = [];
    private readonly HashSet<Type> _excludedServices = [];
    private readonly Dictionary<Type, ReplacementServiceDefinition> _replacementServices = [];
    private IServiceProvider? _serviceProvider;
    private ILoggerFactory? _loggerFactory;
    private ISharpLinkServerAuthenticator? _authenticator;
    private bool _authenticationRequired;
    private readonly List<ISharpLinkServerInterceptor> _interceptors = [];
    private IRpcExceptionMapper? _exceptionMapper;
    private bool _includeExceptionDetails;
    private SharpLinkAdmissionControlOptions? _admissionControlOptions;

    /// <summary>Uses a server listener owned by the built server.</summary>
    /// <param name="transport">The listener used to accept independent connections.</param>
    public SharpLinkServerBuilder UseTransport(IServerTransportListener transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        return this;
    }

    /// <summary>Configures an instance-scoped server authenticator.</summary>
    public SharpLinkServerBuilder UseAuthenticator(ISharpLinkServerAuthenticator authenticator)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        return this;
    }

    /// <summary>Requires authentication and fails <see cref="Build"/> when no authenticator is registered.</summary>
    public SharpLinkServerBuilder RequireAuthentication()
    {
        _authenticationRequired = true;
        return this;
    }

    /// <summary>Adds a server interceptor in registration order.</summary>
    public SharpLinkServerBuilder AddInterceptor(ISharpLinkServerInterceptor interceptor)
    {
        _interceptors.Add(interceptor ?? throw new ArgumentNullException(nameof(interceptor)));
        return this;
    }

    /// <summary>Configures an instance-scoped business exception mapper.</summary>
    public SharpLinkServerBuilder UseExceptionMapper(IRpcExceptionMapper exceptionMapper)
    {
        _exceptionMapper = exceptionMapper ?? throw new ArgumentNullException(nameof(exceptionMapper));
        return this;
    }

    /// <summary>Includes service exception messages in default Internal responses. Disabled by default.</summary>
    public SharpLinkServerBuilder EnableDetailedErrors(bool enabled = true)
    {
        _includeExceptionDetails = enabled;
        return this;
    }

    public SharpLinkServerBuilder UseSerializer(Func<Type,IRpcCodec?>? codecResolver)
    {
        _runtimeContextBuilder.UseCodecResolver(codecResolver);
        return this;
    }

    /// <summary>Registers an explicit codec only for servers built by this builder.</summary>
    public SharpLinkServerBuilder UseCodec<T>(IRpcCodec<T> codec)
    {
        _runtimeContextBuilder.AddCodec(codec);
        return this;
    }

    /// <summary>Configures instance-scoped runtime behavior.</summary>
    public SharpLinkServerBuilder UseRuntime(Action<SharpLinkRuntimeOptions> configure)
    {
        _runtimeContextBuilder.Configure(configure);
        return this;
    }

    /// <summary>Enables bounded active admission control for calls accepted by this server.</summary>
    /// <param name="configure">Configures global, contract, method, partition and queue limits.</param>
    /// <returns>This builder.</returns>
    public SharpLinkServerBuilder UseAdmissionControl(Action<SharpLinkAdmissionControlOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_admissionControlOptions is not null)
            throw new InvalidOperationException("Admission control has already been configured for this builder.");
        var options = new SharpLinkAdmissionControlOptions();
        configure(options);
        options.Validate();
        _admissionControlOptions = options;
        return this;
    }

    public SharpLinkServerBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
        return this;
    }

    public SharpLinkServerBuilder UseBufferWriterPool(Action<BufferWriterPoolOptions> configure)
    {
        _runtimeContextBuilder.ConfigureBufferPool(configure);
        return this;
    }

    public SharpLinkServerBuilder UseStateStoreConcurrency(Action<RuntimeConcurrencyOptions> configure)
    {
        _runtimeContextBuilder.ConfigureStateStores(configure);
        return this;
    }

    public void UseLoggerFactoryIfUnset(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory ??= loggerFactory;
    }

    public SharpLinkServerBuilder UseHeartbeat(TimeSpan checkInterval, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(checkInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        
        if (timeout <= checkInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than check interval.");

        _heartbeatCheckInterval = checkInterval;
        _heartbeatTimeout = timeout;
        return this;
    }

    public SharpLinkServerBuilder UseHeartbeatCheckInterval(TimeSpan checkInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(checkInterval, TimeSpan.Zero);
        if (_heartbeatTimeout <= checkInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than check interval.");

        _heartbeatCheckInterval = checkInterval;
        return this;
    }

    public SharpLinkServerBuilder UseHeartbeatTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (timeout <= _heartbeatCheckInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than check interval.");

        _heartbeatTimeout = timeout;
        return this;
    }

    public SharpLinkServerBuilder UseRpcSessionFlush(int flushSizeThreshold, TimeSpan maxLatency)
    {
        _rpcSessionFlushOptions = RpcSessionFlushOptions.Create(flushSizeThreshold, maxLatency);
        return this;
    }

    /// <summary>Configures per-server protocol safety limits.</summary>
    public SharpLinkServerBuilder UseProtocol(Action<SharpLinkProtocolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _runtimeContextBuilder.Configure(options => configure(options.Protocol));
        return this;
    }

    /// <summary>Uses an application-owned provider for service dependencies and per-call scopes.</summary>
    /// <param name="serviceProvider">The provider used by service factories. It is never disposed by SharpLink.</param>
    public SharpLinkServerBuilder UseServiceProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        return this;
    }

    /// <summary>Disables automatic service exposure for this builder; explicitly enabled and replaced services remain.</summary>
    public SharpLinkServerBuilder DisableAutomaticServiceRegistration()
    {
        _automaticServiceRegistration = false;
        return this;
    }

    /// <summary>Requires and enables the generated service for a contract on this builder.</summary>
    public SharpLinkServerBuilder EnableService<TContract>()
        where TContract : class, IService
    {
        _excludedServices.Remove(typeof(TContract));
        _enabledServices.Add(typeof(TContract));
        return this;
    }

    /// <summary>Excludes the generated service for a contract from this builder.</summary>
    public SharpLinkServerBuilder ExcludeService<TContract>()
        where TContract : class, IService
    {
        _enabledServices.Remove(typeof(TContract));
        _excludedServices.Add(typeof(TContract));
        return this;
    }

    /// <summary>Replaces an automatically generated service with a caller-owned singleton instance.</summary>
    public SharpLinkServerBuilder ReplaceService<TContract>(TContract instance)
        where TContract : class, IService
    {
        ArgumentNullException.ThrowIfNull(instance);
        _replacementServices[typeof(TContract)] = new ReplacementServiceDefinition(
            instance,
            Factory: null,
            SharpLinkServiceLifetime.Singleton,
            CallerOwned: true);
        return this;
    }

    /// <summary>Replaces an automatically generated service with a provider-aware factory.</summary>
    public SharpLinkServerBuilder ReplaceService<TContract>(
        Func<IServiceProvider, TContract> factory,
        SharpLinkServiceLifetime lifetime = SharpLinkServiceLifetime.Singleton)
        where TContract : class, IService
    {
        ArgumentNullException.ThrowIfNull(factory);
        ValidateLifetime(lifetime);
        _replacementServices[typeof(TContract)] = new ReplacementServiceDefinition(
            Instance: null,
            provider => factory(provider) ?? throw new InvalidOperationException(
                $"Replacement factory for '{typeof(TContract).FullName}' returned null."),
            lifetime,
            CallerOwned: false);
        return this;
    }

    public ISharpLinkServer Build()
    {
        var transport = _transport;
        if (transport == null)
            throw new InvalidOperationException("Transport must be set before building the server.");
        if (_authenticationRequired && _authenticator is null)
            throw new InvalidOperationException("RequireAuthentication needs an ISharpLinkServerAuthenticator.");

        var runtimeContext = _runtimeContextBuilder.Build();
        IAsyncDisposable? ownedServiceProvider = null;
        SharpLinkAdmissionController? admissionController = null;
        List<ServiceRegistration>? registrations = null;
        try
        {
            if (transport is IPerformanceProfileAwareTransport profileAwareTransport)
                profileAwareTransport.BindPerformanceProfile(runtimeContext.PerformanceProfile);

            var protocolOptions = runtimeContext.Protocol;
            var serviceProvider = _serviceProvider;
            if (serviceProvider is null)
            {
                var internalProvider = new ServiceCollection().BuildServiceProvider(
                    new ServiceProviderOptions { ValidateScopes = true });
                serviceProvider = internalProvider;
                ownedServiceProvider = internalProvider;
            }

            var manifests = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot();
            if (_admissionControlOptions is not null)
            {
                admissionController = SharpLinkAdmissionController.Create(
                    _admissionControlOptions,
                    manifests);
            }
            var definitions = BuildServiceDefinitions(manifests, serviceProvider);
            registrations = new List<ServiceRegistration>(definitions.Count);
            var registrationsByContract = new Dictionary<long, ServiceRegistration>(definitions.Count);
            foreach (var pair in definitions)
            {
                var registration = pair.Value.Build(serviceProvider);
                registrations.Add(registration);
                registrationsByContract.Add(pair.Key, registration);
            }

            var server = new SharpLinkServer(
                transport,
                registrationsByContract.ToFrozenDictionary(),
                _heartbeatCheckInterval,
                _heartbeatTimeout,
                _loggerFactory ?? NullLoggerFactory.Instance,
                _authenticator,
                _authenticationRequired,
                protocolOptions,
                runtimeContext,
                _rpcSessionFlushOptions,
                _interceptors.ToArray(),
                _exceptionMapper ?? new DefaultRpcExceptionMapper(_includeExceptionDetails),
                ownedServiceProvider,
                serviceProvider,
                manifests,
                admissionController);
            _transport = null;
            return server;
        }
        catch (Exception buildException)
        {
            _transport = null;
            ThrowAfterBuildRollback(
                buildException,
                registrations,
                admissionController,
                ownedServiceProvider,
                runtimeContext,
                transport);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowAfterBuildRollback(
        Exception buildException,
        IReadOnlyList<ServiceRegistration>? registrations,
        SharpLinkAdmissionController? admissionController,
        IAsyncDisposable? ownedServiceProvider,
        SharpLinkRuntimeContext runtimeContext,
        IServerTransportListener transport)
    {
        List<Exception>? cleanupFailures = null;
        if (registrations is not null)
        {
            for (var index = registrations.Count - 1; index >= 0; index--)
            {
                try
                {
                    SharpLinkAsyncCleanup.DisposeSynchronously(registrations[index]);
                }
                catch (Exception cleanupException)
                {
                    (cleanupFailures ??= []).Add(cleanupException);
                }
            }
        }
        if (admissionController is not null)
        {
            try
            {
                SharpLinkAsyncCleanup.DisposeSynchronously(admissionController);
            }
            catch (Exception cleanupException)
            {
                (cleanupFailures ??= []).Add(cleanupException);
            }
        }
        if (ownedServiceProvider is not null)
        {
            try
            {
                SharpLinkAsyncCleanup.DisposeSynchronously(ownedServiceProvider);
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
        try
        {
            SharpLinkAsyncCleanup.DisposeSynchronously(transport);
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

    private Dictionary<long, ServiceRegistrationDefinition> BuildServiceDefinitions(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests,
        IServiceProvider serviceProvider)
    {
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
                    !_replacementServices.ContainsKey(incoming.ContractType))
                {
                    throw CreateBuildConflict("Service", incoming, manifest, existing.Descriptor, existing.Manifest);
                }
                services.TryAdd(incoming.ContractId, (incoming, manifest));
            }
        }

        var definitions = new Dictionary<long, ServiceRegistrationDefinition>();
        var foundEnabled = new HashSet<Type>();
        foreach (var pair in services)
        {
            var service = pair.Value.Descriptor;
            if (_replacementServices.ContainsKey(service.ContractType))
                continue;
            var explicitlyEnabled = _enabledServices.Contains(service.ContractType);
            if (explicitlyEnabled)
                foundEnabled.Add(service.ContractType);
            if ((!_automaticServiceRegistration && !explicitlyEnabled) ||
                _excludedServices.Contains(service.ContractType))
                continue;
            if (!contracts.TryGetValue(service.ContractId, out var contract) ||
                !ReferenceEquals(contract.Descriptor.ContractType, service.ContractType))
            {
                throw new InvalidOperationException(
                    $"Generated service '{service.ImplementationName}' requires contract '{service.ContractName}' " +
                    $"({service.ContractId}), but its contract-owned manifest is not loaded.");
            }
            ValidateDependencies(service, serviceProvider);
            definitions.Add(service.ContractId, new ServiceRegistrationDefinition(
                service.ContractType,
                contract.Descriptor.StubFactory(),
                service.Lifetime,
                service.Activator,
                instance: null,
                callerOwned: false));
        }

        foreach (var replacement in _replacementServices)
        {
            var contract = contracts.Values.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Descriptor.ContractType, replacement.Key));
            if (contract.Descriptor is null)
            {
                throw new InvalidOperationException(
                    $"Generated contract '{replacement.Key.FullName}' required by ReplaceService was not found.");
            }
            var value = replacement.Value;
            definitions[contract.Descriptor.ContractId] = new ServiceRegistrationDefinition(
                replacement.Key,
                contract.Descriptor.StubFactory(),
                value.Lifetime,
                value.Factory,
                value.Instance,
                value.CallerOwned);
            foundEnabled.Add(replacement.Key);
        }

        foreach (var required in _enabledServices)
        {
            if (!foundEnabled.Contains(required))
            {
                throw new InvalidOperationException(
                    $"Generated service for required contract '{required.FullName}' was not found.");
            }
        }
        return definitions;
    }

    private static void ValidateDependencies(
        SharpLinkGeneratedServiceDescriptor service,
        IServiceProvider provider)
    {
        if (service.Dependencies.Count == 0)
            return;
        var availability = provider.GetService<IServiceProviderIsService>();
        if (availability is null)
            return;
        for (var index = 0; index < service.Dependencies.Count; index++)
        {
            var dependency = service.Dependencies[index];
            if (!availability.IsService(dependency))
            {
                throw new InvalidOperationException(
                    $"Required dependency '{dependency.FullName}' for generated RPC service " +
                    $"'{service.ImplementationName}' is not registered.");
            }
        }
    }

    private static void ValidateManifest(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        if (manifest.ApiVersion != SharpLinkGeneratedManifestVersions.Api ||
            manifest.ProtocolVersion != SharpLinkGeneratedManifestVersions.Protocol)
        {
            throw new InvalidOperationException(
                $"Generated manifest '{manifest.OwnerAssembly.FullName}' is incompatible: " +
                $"API={manifest.ApiVersion}, Protocol={manifest.ProtocolVersion}, Generator={manifest.GeneratorVersion}.");
        }
    }

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

    private sealed record ReplacementServiceDefinition(
        object? Instance,
        Func<IServiceProvider, object>? Factory,
        SharpLinkServiceLifetime Lifetime,
        bool CallerOwned);

}
