namespace SharpLink.Server;

internal sealed class ServiceRegistrationDefinition
{
    private readonly Func<IServiceProvider, object>? _factory;
    private readonly object? _instance;
    private readonly bool _callerOwned;
    private readonly bool _providerOwnsService;

    internal ServiceRegistrationDefinition(
        IRpcStub stub,
        ServiceLifetime lifetime,
        Func<IServiceProvider, object>? factory,
        object? instance,
        bool callerOwned,
        bool providerOwnsService)
    {
        Stub = stub ?? throw new ArgumentNullException(nameof(stub));
        Lifetime = lifetime;
        _factory = factory;
        _instance = instance;
        _callerOwned = callerOwned;
        _providerOwnsService = providerOwnsService;
    }

    internal IRpcStub Stub { get; }
    internal ServiceLifetime Lifetime { get; }

    internal ServiceRegistration Build(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        if (_instance is not null)
            return ServiceRegistration.CreateSingleton(Stub, _instance, ownsService: !_callerOwned);

        var factory = _factory ?? throw new InvalidOperationException("Service factory is not configured.");
        if (Lifetime == ServiceLifetime.Singleton)
        {
            return ServiceRegistration.CreateSingletonFactory(
                Stub,
                serviceProvider,
                factory,
                ownsService: !_providerOwnsService);
        }

        var scopeFactory = serviceProvider.GetService<IServiceScopeFactory>() ??
            throw new InvalidOperationException(
                "Scoped and transient SharpLink services require an IServiceScopeFactory.");
        return ServiceRegistration.CreatePerCall(
            Stub,
            scopeFactory,
            factory,
            disposeService: !_providerOwnsService);
    }
}

internal sealed class ServiceRegistration : IAsyncDisposable
{
    private object? _singleton;
    private readonly bool _ownsSingleton;
    private readonly IServiceProvider? _rootProvider;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly Func<IServiceProvider, object>? _factory;
    private readonly bool _disposePerCallService;
    private readonly Lock _singletonGate = new();
    private int _disposed;

    private ServiceRegistration(
        IRpcStub stub,
        object? singleton,
        bool ownsSingleton,
        IServiceProvider? rootProvider,
        IServiceScopeFactory? scopeFactory,
        Func<IServiceProvider, object>? factory,
        bool disposePerCallService)
    {
        Stub = stub;
        _singleton = singleton;
        _ownsSingleton = ownsSingleton;
        _rootProvider = rootProvider;
        _scopeFactory = scopeFactory;
        _factory = factory;
        _disposePerCallService = disposePerCallService;
    }

    internal IRpcStub Stub { get; }

    internal static ServiceRegistration CreateSingleton(
        IRpcStub stub,
        object service,
        bool ownsService)
        => new(stub, service, ownsService, null, null, null, false);

    internal static ServiceRegistration CreateSingletonFactory(
        IRpcStub stub,
        IServiceProvider serviceProvider,
        Func<IServiceProvider, object> factory,
        bool ownsService)
        => new(stub, null, ownsService, serviceProvider, null, factory, false);

    internal static ServiceRegistration CreatePerCall(
        IRpcStub stub,
        IServiceScopeFactory scopeFactory,
        Func<IServiceProvider, object> factory,
        bool disposeService)
        => new(stub, null, false, null, scopeFactory, factory, disposeService);

    internal ValueTask<ServiceLease> AcquireAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_rootProvider is not null)
            return ValueTask.FromResult(new ServiceLease(GetOrCreateSingleton()));
        if (_singleton is not null)
            return ValueTask.FromResult(new ServiceLease(_singleton));

        return AcquirePerCallAsync();
    }

    private async ValueTask<ServiceLease> AcquirePerCallAsync()
    {
        var scope = (_scopeFactory ?? throw new InvalidOperationException("Service scope factory is unavailable."))
            .CreateScope();
        try
        {
            var service = (_factory ?? throw new InvalidOperationException("Service factory is unavailable."))
                .Invoke(scope.ServiceProvider) ??
                throw new InvalidOperationException("The SharpLink service factory returned null.");
            return new ServiceLease(service, scope, _disposePerCallService);
        }
        catch
        {
            await ServiceLease.DisposeScopeAsync(scope).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        object? singleton;
        lock (_singletonGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            singleton = _singleton;
            _singleton = null;
        }
        if (!_ownsSingleton || singleton is null)
            return;
        await ServiceLease.DisposeServiceAsync(singleton).ConfigureAwait(false);
    }

    private object GetOrCreateSingleton()
    {
        var singleton = Volatile.Read(ref _singleton);
        if (singleton is not null)
            return singleton;

        lock (_singletonGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            singleton = _singleton;
            if (singleton is not null)
                return singleton;
            singleton = (_factory ?? throw new InvalidOperationException("Service factory is unavailable."))
                .Invoke(_rootProvider!) ??
                throw new InvalidOperationException("The SharpLink service factory returned null.");
            Volatile.Write(ref _singleton, singleton);
            return singleton;
        }
    }
}

internal readonly struct ServiceLease : IAsyncDisposable
{
    private readonly IServiceScope? _scope;
    private readonly bool _disposeService;

    internal ServiceLease(
        object service,
        IServiceScope? scope = null,
        bool disposeService = false)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
        _scope = scope;
        _disposeService = disposeService;
    }

    internal object Service { get; }
    internal bool RequiresDisposal => _scope is not null;

    public async ValueTask DisposeAsync()
    {
        if (_scope is null)
            return;

        Exception? serviceException = null;
        try
        {
            if (_disposeService)
                await DisposeServiceAsync(Service).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            serviceException = exception;
        }

        try
        {
            await DisposeScopeAsync(_scope).ConfigureAwait(false);
        }
        catch when (serviceException is not null)
        {
        }

        if (serviceException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(serviceException).Throw();
    }

    internal static ValueTask DisposeServiceAsync(object service)
    {
        if (service is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();
        if (service is IDisposable disposable)
            disposable.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static ValueTask DisposeScopeAsync(IServiceScope scope)
    {
        if (scope is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();
        scope.Dispose();
        return ValueTask.CompletedTask;
    }
}
