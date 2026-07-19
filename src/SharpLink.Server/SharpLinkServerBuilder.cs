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
    private readonly Dictionary<long, ServiceRegistrationDefinition> _services = [];
    private readonly List<ServiceDescriptor> _serviceDescriptors = [];
    private IServiceProvider? _serviceProvider;
    private ILoggerFactory? _loggerFactory;
    private ISharpLinkServerAuthenticator? _authenticator;
    private bool _authenticationRequired;
    private readonly List<ISharpLinkServerInterceptor> _interceptors = [];
    private IRpcExceptionMapper? _exceptionMapper;
    private bool _includeExceptionDetails;

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

    /// <summary>Registers a caller-owned singleton service instance.</summary>
    /// <typeparam name="TContract">The generated RPC contract.</typeparam>
    /// <param name="instance">The instance to dispatch. SharpLink does not dispose it.</param>
    public SharpLinkServerBuilder AddService<TContract>(TContract instance)
        where TContract : class, IService
    {
        ArgumentNullException.ThrowIfNull(instance);
        var stub = ResolveStub(instance.GetType(), typeof(TContract));
        AddServiceDefinition(new ServiceRegistrationDefinition(
            stub,
            ServiceLifetime.Singleton,
            factory: null,
            instance,
            callerOwned: true,
            providerOwnsService: false));
        return this;
    }

    /// <summary>Registers a service type with an explicit server-managed lifetime.</summary>
    /// <typeparam name="TContract">The generated RPC contract.</typeparam>
    /// <typeparam name="TService">The implementation constructed from the configured provider.</typeparam>
    /// <param name="lifetime">Singleton by default to preserve the allocation-free dispatch path.</param>
    public SharpLinkServerBuilder AddService<
        TContract,
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TService>(
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TContract : class, IService
        where TService : class, TContract
    {
        ValidateLifetime(lifetime);
        var stub = ResolveStub(typeof(TService), typeof(TContract));
        if (!_serviceDescriptors.Any(static descriptor => descriptor.ServiceType == typeof(TService)))
            _serviceDescriptors.Add(ServiceDescriptor.Describe(typeof(TService), typeof(TService), lifetime));
        AddServiceDefinition(new ServiceRegistrationDefinition(
            stub,
            lifetime,
            static provider => provider.GetRequiredService<TService>(),
            instance: null,
            callerOwned: false,
            providerOwnsService: true));
        return this;
    }

    /// <summary>Registers a provider-aware service factory with an explicit lifetime.</summary>
    /// <typeparam name="TContract">The generated RPC contract.</typeparam>
    /// <param name="factory">Creates a service from the root or current per-call provider.</param>
    /// <param name="lifetime">Scoped by default; scoped and transient instances are disposed after the call or stream.</param>
    public SharpLinkServerBuilder AddService<TContract>(
        Func<IServiceProvider, TContract> factory,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TContract : class, IService
    {
        ArgumentNullException.ThrowIfNull(factory);
        ValidateLifetime(lifetime);
        var stub = ResolveStub(typeof(TContract), typeof(TContract));
        AddServiceDefinition(new ServiceRegistrationDefinition(
            stub,
            lifetime,
            provider => factory(provider),
            instance: null,
            callerOwned: false,
            providerOwnsService: false));
        return this;
    }

    public ISharpLinkServer Build()
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport must be set before building the server.");
        if (_authenticationRequired && _authenticator is null)
            throw new InvalidOperationException("RequireAuthentication needs an ISharpLinkServerAuthenticator.");

        var runtimeContext = _runtimeContextBuilder.Build();
        if (_transport is IPerformanceProfileAwareTransport profileAwareTransport)
            profileAwareTransport.BindPerformanceProfile(runtimeContext.Options.PerformanceProfile);
        var protocolOptions = runtimeContext.Protocol;
        var serviceProvider = _serviceProvider;
        IAsyncDisposable? ownedServiceProvider = null;
        if (serviceProvider is null)
        {
            IServiceCollection internalServices = new ServiceCollection();
            for (var index = 0; index < _serviceDescriptors.Count; index++)
                internalServices.Add(_serviceDescriptors[index]);
            var internalProvider = internalServices.BuildServiceProvider(
                new ServiceProviderOptions { ValidateScopes = true });
            serviceProvider = internalProvider;
            ownedServiceProvider = internalProvider;
        }

        FrozenDictionary<long, ServiceRegistration> registrations;
        try
        {
            registrations = _services.ToDictionary(
                    static pair => pair.Key,
                    pair => pair.Value.Build(serviceProvider))
                .ToFrozenDictionary();
        }
        catch
        {
            if (ownedServiceProvider is IDisposable disposable)
                disposable.Dispose();
            throw;
        }

        return new SharpLinkServer(
            _transport,
            registrations,
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
            ownedServiceProvider);
    }

    private void AddServiceDefinition(ServiceRegistrationDefinition definition)
    {
        if (!_services.TryAdd(definition.Stub.InterfaceHash, definition))
        {
            throw new InvalidOperationException(
                $"RPC contract {definition.Stub.InterfaceHash} is already registered on this builder.");
        }
    }

    internal void AddServiceRegistrationsTo(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        for (var index = 0; index < _serviceDescriptors.Count; index++)
        {
            var descriptor = _serviceDescriptors[index];
            var existing = services.LastOrDefault(candidate =>
                candidate.ServiceType == descriptor.ServiceType);
            if (existing is null)
            {
                services.Add(descriptor);
                continue;
            }
            if (existing.Lifetime != descriptor.Lifetime)
            {
                throw new InvalidOperationException(
                    $"Service '{descriptor.ServiceType.FullName}' is registered as {existing.Lifetime}, " +
                    $"but SharpLink requested {descriptor.Lifetime}.");
            }
        }
    }

    private static IRpcStub ResolveStub(Type serviceType, Type contractType)
    {
        if (GeneratedStubRegistry.TryCreate(serviceType, out var serviceStub) && serviceStub is not null)
            return serviceStub;
        if (GeneratedStubRegistry.TryCreateContract(contractType, out var contractStub) && contractStub is not null)
            return contractStub;
        throw new InvalidOperationException(
            $"Generated stub for service '{serviceType.FullName}' and contract '{contractType.FullName}' is not registered.");
    }

    private static void ValidateLifetime(ServiceLifetime lifetime)
    {
        if (lifetime is not ServiceLifetime.Singleton and
            not ServiceLifetime.Scoped and
            not ServiceLifetime.Transient)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }
    }

}
