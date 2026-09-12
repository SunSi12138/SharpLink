using System.Runtime.CompilerServices;

namespace SharpLink.Server;

internal sealed class ServiceRegistrationDefinition
{
    private readonly Func<IServiceProvider, object>? _factory;
    private readonly object? _instance;
    private readonly bool _callerOwned;

    internal ServiceRegistrationDefinition(
        Type contractType,
        IRpcStub stub,
        SharpLinkServiceLifetime lifetime,
        Func<IServiceProvider, object>? factory,
        object? instance,
        bool callerOwned)
    {
        ContractType = contractType ?? throw new ArgumentNullException(nameof(contractType));
        Stub = stub ?? throw new ArgumentNullException(nameof(stub));
        Lifetime = lifetime;
        _factory = factory;
        _instance = instance;
        _callerOwned = callerOwned;
    }

    internal Type ContractType { get; }
    internal IRpcStub Stub { get; }
    internal SharpLinkServiceLifetime Lifetime { get; }

    internal ServiceRegistration Build(
        IServiceProvider serviceProvider,
        SharpLinkDynamicModule? module = null)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        if (_instance is not null)
        {
            return ServiceRegistration.CreateSingleton(
                ContractType,
                Stub,
                _instance,
                ownsService: !_callerOwned,
                module);
        }

        var factory = _factory ?? throw new InvalidOperationException("Service factory is not configured.");
        if (Lifetime == SharpLinkServiceLifetime.Singleton)
        {
            return ServiceRegistration.CreateSingletonFactory(
                ContractType,
                Stub,
                serviceProvider,
                factory,
                ownsService: true,
                module);
        }

        var scopeFactory = serviceProvider.GetService<IServiceScopeFactory>() ??
            throw new InvalidOperationException(
                "Connection and Call SharpLink services require an IServiceScopeFactory.");
        return Lifetime == SharpLinkServiceLifetime.Connection
            ? ServiceRegistration.CreateConnection(
                ContractType, Stub, scopeFactory, factory, disposeService: true, module)
            : ServiceRegistration.CreatePerCall(
                ContractType, Stub, scopeFactory, factory, disposeService: true, module);
    }
}

internal sealed class ServiceRegistration : IAsyncDisposable
{
    private object? _singleton;
    private readonly bool _ownsSingleton;
    private readonly IServiceProvider? _rootProvider;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly Func<IServiceProvider, object>? _factory;
    private readonly bool _disposeScopedService;
    private readonly Lock _singletonGate = new();
    private int _disposed;

    private ServiceRegistration(
        Type contractType,
        IRpcStub stub,
        SharpLinkServiceLifetime lifetime,
        object? singleton,
        bool ownsSingleton,
        IServiceProvider? rootProvider,
        IServiceScopeFactory? scopeFactory,
        Func<IServiceProvider, object>? factory,
        bool disposeScopedService,
        SharpLinkDynamicModule? module)
    {
        ContractType = contractType;
        Stub = stub;
        Lifetime = lifetime;
        _singleton = singleton;
        _ownsSingleton = ownsSingleton;
        _rootProvider = rootProvider;
        _scopeFactory = scopeFactory;
        _factory = factory;
        _disposeScopedService = disposeScopedService;
        Module = module;
    }

    internal Type ContractType { get; }
    internal IRpcStub Stub { get; }
    internal SharpLinkServiceLifetime Lifetime { get; }
    internal SharpLinkDynamicModule? Module { get; }
    internal CancellationToken ModuleCancellation => Module?.ForcedCancellation ?? CancellationToken.None;
    internal bool AcceptsCalls => Module is null || Module.State == SharpLinkDynamicModuleState.Running;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetStaticSingleton(out object service)
        => TryGetStaticSingleton(generatedBridge: null, requestId: 0, out service);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetStaticSingleton(
        IRpcGeneratedServerBridge? generatedBridge,
        long requestId,
        out object service)
    {
        if (Module is not null || Lifetime != SharpLinkServiceLifetime.Singleton)
        {
            service = null!;
            return false;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        service = Volatile.Read(ref _singleton) ?? GetOrCreateSingleton(generatedBridge, requestId);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAcquireDynamicSingleton(
        bool isStream,
        out object service,
        out SharpLinkDynamicModuleLease moduleLease)
        => TryAcquireDynamicSingleton(
            isStream,
            generatedBridge: null,
            requestId: 0,
            out service,
            out moduleLease);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAcquireDynamicSingleton(
        bool isStream,
        IRpcGeneratedServerBridge? generatedBridge,
        long requestId,
        out object service,
        out SharpLinkDynamicModuleLease moduleLease)
    {
        var module = Module;
        if (module is null || Lifetime != SharpLinkServiceLifetime.Singleton)
        {
            service = null!;
            moduleLease = default;
            return false;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!module.TryAcquire(isStream, out moduleLease))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "RPC module is draining");
        }

        service = Volatile.Read(ref _singleton) ?? GetOrCreateSingleton(generatedBridge, requestId);
        return true;
    }

    internal static ServiceRegistration CreateSingleton(
        Type contractType,
        IRpcStub stub,
        object service,
        bool ownsService,
        SharpLinkDynamicModule? module = null)
        => new(contractType, stub, SharpLinkServiceLifetime.Singleton, service, ownsService,
            null, null, null, false, module);

    internal static ServiceRegistration CreateSingletonFactory(
        Type contractType,
        IRpcStub stub,
        IServiceProvider serviceProvider,
        Func<IServiceProvider, object> factory,
        bool ownsService,
        SharpLinkDynamicModule? module = null)
        => new(contractType, stub, SharpLinkServiceLifetime.Singleton, null, ownsService,
            serviceProvider, null, factory, false, module);

    internal static ServiceRegistration CreateConnection(
        Type contractType,
        IRpcStub stub,
        IServiceScopeFactory scopeFactory,
        Func<IServiceProvider, object> factory,
        bool disposeService,
        SharpLinkDynamicModule? module = null)
        => new(contractType, stub, SharpLinkServiceLifetime.Connection, null, false,
            null, scopeFactory, factory, disposeService, module);

    internal static ServiceRegistration CreatePerCall(
        Type contractType,
        IRpcStub stub,
        IServiceScopeFactory scopeFactory,
        Func<IServiceProvider, object> factory,
        bool disposeService,
        SharpLinkDynamicModule? module = null)
        => new(contractType, stub, SharpLinkServiceLifetime.Call, null, false,
            null, scopeFactory, factory, disposeService, module);

    internal ValueTask<ServiceLease> AcquireAsync(ServerConnectionState connection, bool isStream)
        => AcquireAsync(connection, isStream, generatedBridge: null, requestId: 0);

    internal ValueTask<ServiceLease> AcquireAsync(
        ServerConnectionState connection,
        bool isStream,
        IRpcGeneratedServerBridge? generatedBridge,
        long requestId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        SharpLinkDynamicModuleLease moduleLease = default;
        if (Module is not null && !Module.TryAcquire(isStream, out moduleLease))
        {
            return ValueTask.FromException<ServiceLease>(new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "RPC module is draining"));
        }

        try
        {
            if (_rootProvider is not null)
                return ValueTask.FromResult(new ServiceLease(
                    GetOrCreateSingleton(generatedBridge, requestId),
                    moduleLease: moduleLease));
            if (_singleton is not null)
                return ValueTask.FromResult(new ServiceLease(_singleton, moduleLease: moduleLease));
            if (Lifetime == SharpLinkServiceLifetime.Connection)
                return connection.AcquireServiceAsync(this, moduleLease, generatedBridge, requestId);
            return AcquirePerCallAsync(moduleLease, generatedBridge, requestId);
        }
        catch
        {
            moduleLease.Dispose();
            throw;
        }
    }

    internal ValueTask<ConnectionServiceInstance> CreateConnectionServiceAsync()
        => CreateConnectionServiceAsync(generatedBridge: null, requestId: 0);

    internal async ValueTask<ConnectionServiceInstance> CreateConnectionServiceAsync(
        IRpcGeneratedServerBridge? generatedBridge,
        long requestId)
    {
        EnsureUserCodeEntry(generatedBridge, requestId);
        var scope = (_scopeFactory ?? throw new InvalidOperationException("Service scope factory is unavailable."))
            .CreateScope();
        try
        {
            EnsureUserCodeEntry(generatedBridge, requestId);
            var service = (_factory ?? throw new InvalidOperationException("Service factory is unavailable."))
                .Invoke(scope.ServiceProvider) ??
                throw new InvalidOperationException("The SharpLink service factory returned null.");
            return new ConnectionServiceInstance(service, scope, _disposeScopedService);
        }
        catch (Exception activationException)
        {
            try
            {
                await ServiceLease.DisposeScopeAsync(scope).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(activationException, cleanupException);
            }
            throw;
        }
    }

    private async ValueTask<ServiceLease> AcquirePerCallAsync(
        SharpLinkDynamicModuleLease moduleLease,
        IRpcGeneratedServerBridge? generatedBridge,
        long requestId)
    {
        IServiceScope? scope = null;
        try
        {
            EnsureUserCodeEntry(generatedBridge, requestId);
            scope = (_scopeFactory ?? throw new InvalidOperationException("Service scope factory is unavailable."))
                .CreateScope();
            EnsureUserCodeEntry(generatedBridge, requestId);
            var service = (_factory ?? throw new InvalidOperationException("Service factory is unavailable."))
                .Invoke(scope.ServiceProvider) ??
                throw new InvalidOperationException("The SharpLink service factory returned null.");
            return new ServiceLease(service, scope, _disposeScopedService, moduleLease);
        }
        catch (Exception activationException)
        {
            moduleLease.Dispose();
            try
            {
                if (scope is not null)
                    await ServiceLease.DisposeScopeAsync(scope).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(activationException, cleanupException);
            }
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
        if (_ownsSingleton && singleton is not null)
            await ServiceLease.DisposeServiceAsync(singleton).ConfigureAwait(false);
    }

    private object GetOrCreateSingleton(
        IRpcGeneratedServerBridge? generatedBridge,
        long requestId)
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
            EnsureUserCodeEntry(generatedBridge, requestId);
            singleton = (_factory ?? throw new InvalidOperationException("Service factory is unavailable."))
                .Invoke(_rootProvider!) ??
                throw new InvalidOperationException("The SharpLink service factory returned null.");
            Volatile.Write(ref _singleton, singleton);
            return singleton;
        }
    }

    private static void EnsureUserCodeEntry(
        IRpcGeneratedServerBridge? generatedBridge,
        long requestId)
        => generatedBridge?.EnsureUserCodeEntry(requestId);
}

internal sealed class ConnectionServiceInstance : IAsyncDisposable
{
    private readonly IServiceScope _scope;
    private readonly bool _disposeService;
    private int _disposed;

    internal ConnectionServiceInstance(object service, IServiceScope scope, bool disposeService)
    {
        Service = service;
        _scope = scope;
        _disposeService = disposeService;
    }

    internal object Service { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Exception? serviceException = null;
        try
        {
            if (_disposeService)
                await ServiceLease.DisposeServiceAsync(Service).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            serviceException = exception;
        }
        Exception? scopeException = null;
        try
        {
            await ServiceLease.DisposeScopeAsync(_scope).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            scopeException = exception;
        }
        if (serviceException is not null && scopeException is not null)
            throw new AggregateException(serviceException, scopeException);
        if (serviceException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(serviceException).Throw();
        if (scopeException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(scopeException).Throw();
    }
}

internal readonly struct ServiceLease : IAsyncDisposable
{
    private readonly IServiceScope? _scope;
    private readonly bool _disposeService;
    private readonly SharpLinkDynamicModuleLease _moduleLease;

    internal ServiceLease(
        object service,
        IServiceScope? scope = null,
        bool disposeService = false,
        SharpLinkDynamicModuleLease moduleLease = default)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
        _scope = scope;
        _disposeService = disposeService;
        _moduleLease = moduleLease;
    }

    internal object Service { get; }
    internal bool RequiresDisposal => _scope is not null || _moduleLease.IsAcquired;

    public async ValueTask DisposeAsync()
    {
        Exception? serviceException = null;
        try
        {
            if (_scope is not null && _disposeService)
                await DisposeServiceAsync(Service).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            serviceException = exception;
        }

        Exception? scopeException = null;
        try
        {
            if (_scope is not null)
                await DisposeScopeAsync(_scope).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            scopeException = exception;
        }
        finally
        {
            _moduleLease.Dispose();
        }

        if (serviceException is not null && scopeException is not null)
            throw new AggregateException(serviceException, scopeException);
        if (serviceException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(serviceException).Throw();
        if (scopeException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(scopeException).Throw();
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
