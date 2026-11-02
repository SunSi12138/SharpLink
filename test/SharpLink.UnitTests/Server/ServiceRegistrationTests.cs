using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.RollbackPlugin;
using SharpLink.Server;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public partial class ServiceRegistrationTests
{
    [Test]
    public async Task ScopedRegistrationsShouldNotAllocateSingletonGates()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var stub = new StubMarker();
        var call = ServiceRegistration.CreatePerCall(
            typeof(object), stub, scopeFactory, static _ => new object(), disposeService: false);
        var connection = ServiceRegistration.CreateConnection(
            typeof(object), stub, scopeFactory, static _ => new object(), disposeService: false);
        var instance = new object();
        var singleton = ServiceRegistration.CreateSingleton(
            typeof(object), stub, instance, ownsService: false);
        var singletonFactory = ServiceRegistration.CreateSingletonFactory(
            typeof(object), stub, provider, static _ => new object(), ownsService: false);
        var gateField = typeof(ServiceRegistration).GetField(
            "_singletonGate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("missing singleton gate");

        Ensure(gateField.GetValue(call) is null, "call-lifetime registration must not allocate a Lock");
        Ensure(gateField.GetValue(connection) is null,
            "connection-lifetime registration must not allocate a Lock");
        Ensure(gateField.GetValue(singleton) is Lock && gateField.GetValue(singletonFactory) is Lock,
            "singleton registration must retain the gate for disposal and lazy activation");

        Ensure(!call.TryGetStaticSingleton(out _),
            "call-lifetime registration must not take the singleton dispatch path");
        Ensure(!connection.TryGetStaticSingleton(out _),
            "connection-lifetime registration must not take the singleton dispatch path");
        Ensure(!call.TryAcquireDynamicSingleton(isStream: false, out _, out _),
            "non-module call registration must not take the dynamic singleton path");
        Ensure(singleton.TryGetStaticSingleton(out var resolved) && ReferenceEquals(instance, resolved),
            "instance-backed singleton must be resolved before falling back to AcquireAsync");
        Ensure(singletonFactory.TryGetStaticSingleton(out _),
            "factory-backed singleton must be resolved before falling back to AcquireAsync");

        var lease = await call.AcquireAsync(null!, isStream: false);
        Ensure(lease.Service is not null,
            "root-provider-free call registration must use per-call acquisition");
        await lease.DisposeAsync();
        await call.DisposeAsync();
        await connection.DisposeAsync();
        await singleton.DisposeAsync();
        await singletonFactory.DisposeAsync();
    }

    [Test]
    public async Task DynamicInstanceSingletonShouldBeAcquiredBeforeFallbackPath()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest(typeof(ServiceRegistrationTests).Assembly);
        using var codecRegistration = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(
            typeof(ServiceRegistrationTests).Assembly, manifest, codecRegistration);
        var instance = new object();
        var registration = ServiceRegistration.CreateSingleton(
            typeof(object), new StubMarker(), instance, ownsService: false, module);

        Ensure(registration.HasModule, "dynamic registration must cache module presence");
        Ensure(registration.ModuleCancellation == module.ForcedCancellation,
            "dynamic registration must expose the module cancellation token");
        Ensure(registration.AcceptsCalls, "running dynamic module must accept calls");
        Ensure(!registration.TryGetStaticSingleton(out _),
            "dynamic singleton cannot take the static singleton path");
        Ensure(registration.TryAcquireDynamicSingleton(
                isStream: false, out var resolved, out var lease),
            "dynamic singleton must be acquired before the fallback path");
        try
        {
            Ensure(ReferenceEquals(instance, resolved), "dynamic singleton instance identity");
            Ensure(module.RemainingCalls == 1, "dynamic invocation must hold a module lease");
        }
        finally
        {
            lease.Dispose();
        }
        Ensure(module.RemainingCalls == 0, "dynamic module lease must be released");
        module.TryBeginDraining();
        Ensure(!registration.AcceptsCalls, "draining dynamic module must reject calls");
        await registration.DisposeAsync();
    }

    [Test]
    [Arguments(SharpLinkServiceLifetime.Singleton)]
    [Arguments(SharpLinkServiceLifetime.Connection)]
    [Arguments(SharpLinkServiceLifetime.Call)]
    public async Task RetiredSelectedRegistrationShouldRejectBeforeDisposedAccess(SharpLinkServiceLifetime lifetime)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest(typeof(ServiceRegistrationTests).Assembly);
        using var codecRegistration = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(
            typeof(ServiceRegistrationTests).Assembly, manifest, codecRegistration);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var stub = new StubMarker();
        var registration = lifetime switch
        {
            SharpLinkServiceLifetime.Singleton => ServiceRegistration.CreateSingleton(
                typeof(object), stub, new object(), ownsService: false, module),
            SharpLinkServiceLifetime.Connection => ServiceRegistration.CreateConnection(
                typeof(object), stub, scopeFactory, static _ => new object(), disposeService: false, module),
            _ => ServiceRegistration.CreatePerCall(
                typeof(object), stub, scopeFactory, static _ => new object(), disposeService: false, module)
        };
        Ensure(registration.AcceptsCalls, "capture a running registration before the retirement race");
        module.TryBeginDraining();
        await registration.DisposeAsync();
        module.MarkReleased();
        try
        {
            if (lifetime == SharpLinkServiceLifetime.Singleton)
                _ = registration.TryAcquireDynamicSingleton(isStream: false, out _, out _);
            else
                _ = await registration.AcquireAsync(null!, isStream: false);
            throw new Exception("assert failed: retired registration must reject acquisition");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable,
                "normal retirement must not surface disposed-object Internal");
        }
        Ensure(module.RemainingCalls == 0, "retirement rejection must not leak a module lease");
    }

    [Test]
    public async Task DynamicServiceDispatchShouldLookupMethodDescriptorOnlyOnce()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest(typeof(ServiceRegistrationTests).Assembly);
        using var codecRegistration = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(
            typeof(ServiceRegistrationTests).Assembly, manifest, codecRegistration);

        await using var server = CreateServer();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = new RpcSession(
            RpcSessionTestFixture.Transport(
                Guid.NewGuid().ToString("N"), input.Reader, output.Writer),
            RpcSessionTestFixture.ServerOptions());
        RpcSessionTestFixture.CompleteHandshake(session);
        var connection = new ServerConnectionState(
            session,
            new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(
                RpcSessionTestFixture.RuntimeContext.Concurrency),
            CancellationToken.None,
            RpcSessionTestFixture.RuntimeContext.TimeProvider);
        Ensure(connection.MarkReady(null), "test connection ready");
        try
        {
            var successfulStub = new StubMarker(RpcMethodKind.Unary);
            var singleton = ServiceRegistration.CreateSingleton(
                typeof(object), successfulStub, new object(), ownsService: false, module);
            await InvokeRegisteredServiceAsync(server, singleton, connection, requestId: 101);
            Ensure(successfulStub.DescriptorLookups == 1,
                "successful dynamic singleton invocation must probe its descriptor exactly once");
            Ensure(module.RemainingCalls == 0, "successful invocation releases its module lease");
            await singleton.DisposeAsync();

            var failingStub = new StubMarker(RpcMethodKind.Unary);
            var call = ServiceRegistration.CreatePerCall(
                typeof(object), failingStub, new ThrowingScopeFactory("scope cleanup failed"),
                static _ => throw new InvalidOperationException("activation failed"),
                disposeService: true, module);
            var failure = await CaptureAsync(() =>
                InvokeRegisteredServiceAsync(server, call, connection, requestId: 102));
            Ensure(ContainsMessage(failure, "activation failed"),
                "failed dynamic call must preserve its activation error");
            Ensure(ContainsMessage(failure, "scope cleanup failed"),
                "failed dynamic call must preserve its cleanup error");
            Ensure(failingStub.DescriptorLookups == 1,
                "failed asynchronous acquisition must reuse its descriptor for telemetry");
            Ensure(module.RemainingCalls == 0, "failed acquisition releases its module lease");
            await call.DisposeAsync();
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static ValueTask InvokeRegisteredServiceAsync(
        SharpLinkServer server,
        ServiceRegistration registration,
        ServerConnectionState connection,
        long requestId)
    {
        var method = typeof(SharpLinkServer).GetMethod(
            "InvokeServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SharpLinkServer), "InvokeServiceAsync");
        return (ValueTask)(method.Invoke(server,
        [
            new ResolvedMethodCall(registration, registration.Stub.ResolveMethodFacts(17)),
            connection,
            connection.Session,
            requestId,
            ReadOnlySequence<byte>.Empty,
            null,
            CancellationToken.None,
            new SharpLinkCallContextSnapshot(connection.Session.Id, authentication: null)
        ]) ?? throw new InvalidOperationException("server invocation did not return a ValueTask"));
    }

    [Test]
    public async Task ActivationRollbackShouldPreserveActivationAndScopeFailures()
    {
        var registration = ServiceRegistration.CreatePerCall(
            typeof(object),
            new StubMarker(),
            new ThrowingScopeFactory("scope cleanup failed"),
            static _ => throw new InvalidOperationException("activation failed"),
            disposeService: true);

        var failure = await CaptureAsync(() => registration.AcquireAsync(null!, isStream: false));

        Ensure(ContainsMessage(failure, "activation failed"),
            "activation rollback must retain the primary activation failure");
        Ensure(ContainsMessage(failure, "scope cleanup failed"),
            "activation rollback must retain the scope cleanup failure");
    }

    [Test]
    public async Task ScopeCreationFailureShouldReleaseDynamicPerCallLease()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest(typeof(ServiceRegistrationTests).Assembly);
        using var codecRegistration = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(
            typeof(ServiceRegistrationTests).Assembly,
            manifest,
            codecRegistration);
        var registration = ServiceRegistration.CreatePerCall(
            typeof(object),
            new StubMarker(),
            new ScopeCreationThrowingFactory(),
            static _ => new object(),
            disposeService: true,
            module);

        var failure = await CaptureAsync(() => registration.AcquireAsync(null!, isStream: false));

        Ensure(ContainsMessage(failure, "scope creation failed"), "scope creation failure preserved");
        Ensure(module.RemainingCalls == 0, "failed scope creation must release its module call lease");
    }

    [Test]
    public async Task ConnectionActivationRollbackShouldPreserveActivationAndScopeFailures()
    {
        var registration = ServiceRegistration.CreateConnection(
            typeof(object),
            new StubMarker(),
            new ThrowingScopeFactory("connection activation scope cleanup failed"),
            static _ => throw new InvalidOperationException("connection activation failed"),
            disposeService: true);

        var failure = await CaptureAsync(registration.CreateConnectionServiceAsync);

        Ensure(ContainsMessage(failure, "connection activation failed"),
            "connection activation rollback must retain the primary activation failure");
        Ensure(ContainsMessage(failure, "connection activation scope cleanup failed"),
            "connection activation rollback must retain the scope cleanup failure");
    }

    [Test]
    public async Task ServiceLeaseShouldPreserveServiceAndScopeDisposalFailures()
    {
        var lease = new ServiceLease(
            new ThrowingAsyncDisposable("service disposal failed"),
            new ThrowingScope("scope disposal failed"),
            disposeService: true);

        var failure = await CaptureAsync(lease.DisposeAsync);

        Ensure(ContainsMessage(failure, "service disposal failed"),
            "lease cleanup must retain the service disposal failure");
        Ensure(ContainsMessage(failure, "scope disposal failed"),
            "lease cleanup must retain the scope disposal failure");
    }

    [Test]
    public async Task ConnectionServiceShouldPreserveServiceAndScopeDisposalFailures()
    {
        var instance = new ConnectionServiceInstance(
            new ThrowingAsyncDisposable("connection service disposal failed"),
            new ThrowingScope("connection scope disposal failed"),
            disposeService: true);

        var failure = await CaptureAsync(instance.DisposeAsync);

        Ensure(ContainsMessage(failure, "connection service disposal failed"),
            "connection cleanup must retain the service disposal failure");
        Ensure(ContainsMessage(failure, "connection scope disposal failed"),
            "connection cleanup must retain the scope cleanup failure");
    }

    [Test]
    public async Task ServerServiceCleanupShouldPreserveEveryRegistrationAndProviderFailure()
    {
        var first = ServiceRegistration.CreateSingleton(
            typeof(object), new StubMarker(),
            new ThrowingAsyncDisposable("first singleton cleanup failed"), ownsService: true);
        var second = ServiceRegistration.CreateSingleton(
            typeof(string), new StubMarker(),
            new ThrowingAsyncDisposable("second singleton cleanup failed"), ownsService: true);
        var cleanup = new ServerServiceCleanup(
            [first, second],
            new ThrowingAsyncDisposable("provider cleanup failed"));

        var failure = await CaptureAsync(cleanup.DisposeAsync);

        Ensure(ContainsMessage(failure, "first singleton cleanup failed"),
            "server cleanup must retain the first registration failure");
        Ensure(ContainsMessage(failure, "second singleton cleanup failed"),
            "server cleanup must retain the second registration failure");
        Ensure(ContainsMessage(failure, "provider cleanup failed"),
            "server cleanup must retain the owned provider failure");
    }

    [Test]
    public async Task DynamicModuleReleaseShouldPreserveEveryServiceFailure()
    {
        var server = CreateServer();
        var module = AddDynamicModule(server, "module-release",
            CreateThrowingRegistration(typeof(object), "first module cleanup failed"),
            CreateThrowingRegistration(typeof(string), "second module cleanup failed"));

        try
        {
            module.TryBeginDraining();
            var failure = await CaptureAsync(() => InvokePrivateAsync(
                server,
                "ReleaseModuleAsync",
                module.Assembly,
                module));

            Ensure(ContainsMessage(failure, "first module cleanup failed"),
                "module release must retain its first service failure");
            Ensure(ContainsMessage(failure, "second module cleanup failed"),
                "module release must retain its second service failure");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task DynamicModuleShutdownShouldPreserveEveryModuleFailure()
    {
        var server = CreateServer();
        AddDynamicModule(server, "first-module",
            CreateThrowingRegistration(typeof(object), "first dynamic module failed"));
        AddDynamicModule(server, "second-module",
            CreateThrowingRegistration(typeof(string), "second dynamic module failed"));

        try
        {
            var failure = await CaptureAsync(() => InvokePrivateAsync(
                server,
                "ReleaseDrainedDynamicModulesAsync"));

            Ensure(ContainsMessage(failure, "first dynamic module failed"),
                "dynamic shutdown must retain its first module failure");
            Ensure(ContainsMessage(failure, "second dynamic module failed"),
                "dynamic shutdown must retain its second module failure");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Test]
    // The scenario owns both a generated-catalog identity and RollbackState process state.
    [NotInParallel(new[] { "generated-catalog", "rollback-plugin" })]
    public async Task RegisteredServiceCleanupShouldPreserveDynamicAndFrameworkOwnedStaticFailures(CancellationToken testCancellationToken)
    {
        var owner = new RegisteredCleanupFixture();
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            owner.Manifest = new StaticCleanupManifest();
            owner.CatalogAttempted = true;
            SharpLinkGeneratedAssemblyCatalog.Register(owner.Manifest);
            owner.StaticService = new RegisteredCleanupService(owner, isStatic: true,
                new InvalidOperationException("static ownership cleanup failed"));
            owner.Listener = new RegisteredCleanupListener();
            var builder = SharpLinkServerBuilder.Create()
                .UseTransport(owner.Listener)
                .DisableAutomaticServiceRegistration()
                .UseServiceProvider(new EmptyServiceProvider())
                .ReplaceService<IStaticCleanupContract>(owner.StaticService);
            MarkReplacementFrameworkOwned(builder, typeof(IStaticCleanupContract));
            owner.BuildAttempted = true;
            owner.Endpoint = builder.Build(); // Retain the real return before the concrete cast.
            owner.Server = (SharpLinkServer)owner.Endpoint;
            owner.DynamicService = new RegisteredCleanupService(owner, isStatic: false,
                new InvalidOperationException("dynamic ownership cleanup failed"));
            owner.DynamicRegistration = ServiceRegistration.CreateSingleton(typeof(string), new StubMarker(),
                owner.DynamicService, ownsService: true);
            owner.DynamicOffered = true;
            owner.Module = AddDynamicModule(owner.Server, "dynamic-ownership", owner.DynamicRegistration);
            owner.ModuleAssembly = owner.Module.Assembly;

            owner.BeginDispose();
            await owner.WaitForCompletion(testCancellationToken);
            owner.AssertExpectedCleanup();
        }
        catch (Exception failure) { owner.Failures.Add(failure); }
        finally { await owner.FinishAsync(testCancellationToken); }
        owner.ThrowIfFailed();
    }

    private static async Task<Exception> CaptureAsync(Func<ValueTask> action)
    {
        try
        {
            await action();
            throw new Exception("expected operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            throw new Exception("expected operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task InvokePrivateAsync(object target, string methodName, params object[] arguments)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingMethodException(target.GetType().FullName, methodName);
        await ((Task?)method.Invoke(target, arguments) ??
            throw new InvalidOperationException($"{methodName} did not return a Task."));
    }

    private static SharpLinkServer CreateServer()
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTransport(new NoopListener())
            .DisableAutomaticServiceRegistration()
            .UseServiceProvider(new EmptyServiceProvider())
            .Build();

    private static void MarkReplacementFrameworkOwned(SharpLinkServerBuilder builder, Type contractType)
    {
        var definitionsField = typeof(SharpLinkServerBuilder).GetField(
            "_replacementServices",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new Exception("cannot find Server Builder replacement services");
        var definitions = (System.Collections.IDictionary)(definitionsField.GetValue(builder) ??
            throw new Exception("cannot read Server Builder replacement services"));
        var replacement = definitions[contractType] ??
            throw new Exception($"cannot find replacement for '{contractType.FullName}'");
        var replacementType = replacement.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var instance = replacementType.GetProperty("Instance", flags)?.GetValue(replacement);
        var factory = replacementType.GetProperty("Factory", flags)?.GetValue(replacement);
        var lifetime = replacementType.GetProperty("Lifetime", flags)?.GetValue(replacement);
        ConstructorInfo? constructor = null;
        foreach (var candidate in replacementType.GetConstructors(flags))
        {
            if (candidate.GetParameters().Length == 4)
            {
                constructor = candidate;
                break;
            }
        }
        if (constructor is null || lifetime is null)
            throw new Exception("cannot construct framework-owned Server Builder replacement");

        definitions[contractType] = constructor.Invoke([instance, factory, lifetime, false]);
    }

    private static SharpLinkDynamicModule AddDynamicModule(
        SharpLinkServer server,
        string name,
        params ServiceRegistration[] registrations)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"SharpLink.UnitTests.{name}.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var locatorConstructor = typeof(SharpLinkGeneratedAssemblyManifestAttribute).GetConstructor(
            [typeof(Type), typeof(int), typeof(int), typeof(string), typeof(string)]) ??
            throw new Exception("cannot find current SharpLink manifest locator constructor");
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            locatorConstructor,
            [
                typeof(EmptyManifest),
                SharpLinkGeneratedManifestVersions.Api,
                SharpLinkGeneratedManifestVersions.Protocol,
                "test",
                SharpLinkGeneratedManifestVersions.AbiIdentity
            ]));
        var manifest = new EmptyManifest(assembly);
        var runtime = (SharpLinkRuntimeContext)GetPrivateField(server, "_runtimeContext");
        using var preparation = runtime.Domain.BeginPreparation();
        var prepared = PreparedGeneratedManifest.Capture(manifest, preparation);
        var codecRegistration = runtime.PrepareGeneratedManifest(prepared);
        runtime.AdoptGeneratedManifest(codecRegistration);
        var module = new SharpLinkDynamicModule(assembly, prepared, codecRegistration);
        var detachedCharge = preparation.ReserveCollection(registrations.Length);
        var detachedOwner = new ServerDetachedServices(registrations, preparation, detachedCharge);

        var registry = (ServerServiceModuleRegistry)GetPrivateField(server, "_serviceModuleRegistry");
        lock (registry.Gate)
        {
            registry.DynamicModules.Add(assembly, module);
            registry.DetachedModuleServices.Add(module, detachedOwner);
        }
        return module;
    }

    private static object GetPrivateField(object target, string fieldName)
        => target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) ??
           throw new MissingFieldException(target.GetType().FullName, fieldName);

    private static ServiceRegistration CreateThrowingRegistration(Type contractType, string message)
        => ServiceRegistration.CreateSingleton(
            contractType,
            new StubMarker(),
            new ThrowingAsyncDisposable(message),
            ownsService: true);

    private static async Task<Exception> CaptureAsync<T>(Func<ValueTask<T>> action)
    {
        try
        {
            await action();
            throw new Exception("expected operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static bool ContainsMessage(Exception exception, string message)
    {
        if (exception.Message == message)
            return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (ContainsMessage(inner, message))
                    return true;
            }
            return false;
        }
        return exception.InnerException is { } nested && ContainsMessage(nested, message);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ThrowingScopeFactory(string message) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new ThrowingScope(message);
    }

    private sealed class ScopeCreationThrowingFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
            => throw new InvalidOperationException("scope creation failed");
    }

    private sealed class ThrowingScope(string message) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } = new EmptyServiceProvider();

        public void Dispose() => throw new InvalidOperationException(message);

        public ValueTask DisposeAsync()
            => ValueTask.FromException(new InvalidOperationException(message));
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class ThrowingAsyncDisposable(string message) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
            => ValueTask.FromException(new InvalidOperationException(message));
    }

    private interface IStaticCleanupContract : IService;

    private sealed class ThrowingStaticCleanupService(string message) : IStaticCleanupContract, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.FromException(new InvalidOperationException(message));
        }
    }

    private sealed class NoopListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyManifest(Assembly ownerAssembly) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public RpcHash128 RpcAssemblyHash => new(0x736572766963652dUL, 0x656d7074792d7631UL);
        public string CompileTimeDescriptor => "test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class StaticCleanupManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(StaticCleanupManifest).Assembly;
        public RpcHash128 RpcAssemblyHash => new(0x736572766963652dUL, 0x636c65616e75702dUL);
        public string CompileTimeDescriptor => "service-cleanup";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } =
        [
            new SharpLinkGeneratedContractDescriptor(
                typeof(IStaticCleanupContract),
                typeof(IStaticCleanupContract).FullName!,
                91_004,
                new string('c', 64),
                [],
                static (_, _) => throw new NotSupportedException(),
                static _ => new StubMarker(interfaceHash: 91_004))
        ];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class StubMarker(RpcMethodKind? descriptorKind = null, long interfaceHash = 1) : IRpcStub
    {
        public RpcMethodFacts ResolveMethodFacts(long methodHash)
            => TryGetMethodDescriptor(methodHash, out var descriptor)
                ? MethodFactsTestHelper.FromDescriptor(descriptor)
                : RpcMethodFacts.UnknownMethod;

        private int _descriptorLookups;
        internal int DescriptorLookups => Volatile.Read(ref _descriptorLookups);

        public bool TryGetMethodDescriptor(long methodHash, out RpcMethodDescriptor descriptor)
        {
            Interlocked.Increment(ref _descriptorLookups);
            if (descriptorKind is not { } kind)
            {
                descriptor = default;
                return false;
            }

            descriptor = new RpcMethodDescriptor(
                InterfaceHash, methodHash, kind,
                HasResponsePayload: false,
                HasClientStreams: false,
                HasMethodTimeout: false,
                MethodTimeout: null);
            return true;
        }

        public long InterfaceHash => interfaceHash;

        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts methodHash,
            long requestId, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output)
            => ValueTask.CompletedTask;

        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    // Concrete receipts for the one selected registered-service cleanup fixture.
    // These callbacks always finish synchronously; every invocation still creates
    // and retains its real source, so a duplicate Dispose is never cached away.
    private sealed class RegisteredCleanupService(
        RegisteredCleanupFixture owner, bool isStatic, Exception failure) : IStaticCleanupContract, IAsyncDisposable
    {
        internal Exception Failure { get; } = failure;
        internal readonly List<Task> Sources = [];
        internal readonly List<AggregateException> Outcomes = [];
        public ValueTask DisposeAsync()
        {
            var source = Task.FromException(Failure);
            lock (owner.ReceiptGate)
            {
                Sources.Add(source);
                Outcomes.Add(source.Exception!);
                owner.CallbackOrder.Add(isStatic);
            }
            return new ValueTask(source);
        }
    }

    private sealed class RegisteredCleanupListener : IServerTransportListener
    {
        private readonly NoopListener _inner = new();
        internal readonly List<Task> Sources = [];
        internal readonly List<Task<Exception?>> Outcomes = [];
        internal readonly List<Exception> InvocationFailures = [];
        public EndPoint? LocalEndPoint => _inner.LocalEndPoint;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => _inner.AcceptAsync(cancellationToken);
        public ValueTask DisposeAsync()
        {
            lock (Sources)
            {
                try
                {
                    var source = _inner.DisposeAsync().AsTask();
                    Sources.Add(source);
                    Outcomes.Add(RegisteredCleanupFixture.ReadOutcome(source));
                    return new ValueTask(source);
                }
                catch (Exception failure) { InvocationFailures.Add(failure); throw; }
            }
        }
    }

    private sealed class RegisteredCleanupFixture
    {
        private static readonly List<RegisteredCleanupFixture> Retained = [];
        internal readonly object ReceiptGate = new();
        internal readonly List<bool> CallbackOrder = [];
        internal readonly List<Exception> Failures = [];
        internal StaticCleanupManifest? Manifest;
        internal bool CatalogAttempted;
        internal RegisteredCleanupService? StaticService;
        internal RegisteredCleanupService? DynamicService;
        internal RegisteredCleanupListener? Listener;
        internal bool BuildAttempted;
        internal ISharpLinkServer? Endpoint;
        internal SharpLinkServer? Server;
        internal ServiceRegistration? DynamicRegistration;
        internal bool DynamicOffered;
        internal SharpLinkDynamicModule? Module;
        internal Assembly? ModuleAssembly;
        private bool _disposeAttempted;
        private Task? _dispose;
        private Task<Exception?>? _disposeOutcome;
        private Task? _physical;
        private Task<Exception?>? _physicalOutcome;
        private Task? _bodyWaiter;
        private Task<Exception?>? _bodyWaiterOutcome;
        private Task? _finishWaiter;
        private Task<Exception?>? _finishWaiterOutcome;
        private Task? _unofferedRegistration;
        private Task<Exception?>? _unofferedRegistrationOutcome;
        // Fixed source stages, not an extensible task registry. The collector is
        // rooted immediately and continues observing if the test waiter cancels.
        private (Task Source, Task<Exception?> Outcome)[] _lifecycle = [];
        private (Task Source, Task<Exception?> Outcome)[] _retirement = [];
        private bool _captured;
        private bool _accepted;

        internal static async Task<Exception?> ReadOutcome(Task source)
        {
            try { await source; return null; }
            catch (Exception failure) { return source.Exception is { } full ? full : failure; }
        }

        internal void BeginDispose()
        {
            if (_disposeAttempted || Endpoint is null) return;
            _disposeAttempted = true;
            try
            {
                _dispose = Endpoint.DisposeAsync().AsTask();
                _disposeOutcome = ReadOutcome(_dispose);
                _physical = ObservePhysicalAsync();
                _physicalOutcome = ReadOutcome(_physical);
            }
            catch (Exception failure) { Failures.Add(failure); }
        }

        private async Task ObservePhysicalAsync()
        {
            await _disposeOutcome!; // outcome-as-data, not a success inference
            if (Server is null) return;
            var lifecycle = Server.LifecycleForDiagnostics;
            Task?[] stages;
            lock (lifecycle.StateGate)
            {
                stages = [
                    (Task?)ReadPrivate(lifecycle, "_stopTask"),
                    lifecycle.DeferredServiceCleanupTaskForDiagnostics,
                    lifecycle.StrictPhysicalDrainForDiagnostics.Drain,
                    lifecycle.ShutdownCleanupObserverTaskForDiagnostics,
                    (Task?)ReadPrivate(lifecycle, "_serviceCleanupObserver"),
                    lifecycle.TerminalFailureObserverTaskForDiagnostics,
                    Server.WaitForShutdownAsync()
                ];
            }
            _lifecycle = Capture(stages);
            // Stop may have returned at its budget while service cleanup still
            // publishes retirement sources. Join the real first wave BEFORE
            // sampling those later module fields; a canceled test waiter does
            // not cancel this observation-only collector.
            foreach (var source in _lifecycle) await source.Outcome;
            if (Module is not null)
            {
                var registry = (ServerServiceModuleRegistry)GetPrivateField(Server, "_serviceModuleRegistry");
                lock (registry.Gate)
                    _retirement = Capture([
                        Module.RetirementOperation, Module.RetirementTracking,
                        Module.RetirementObservation, Module.CleanupCompletion?.Task,
                        Module.RetirementControl?.Driver.Task
                    ]);
            }
            _captured = true; // original service cleanup has closed retirement publication
            foreach (var source in _retirement) await source.Outcome;
        }

        private static (Task Source, Task<Exception?> Outcome)[] Capture(Task?[] sources)
        {
            var result = new List<(Task Source, Task<Exception?> Outcome)>();
            foreach (var source in sources)
                if (source is not null) result.Add((source, ReadOutcome(source)));
            return result.ToArray();
        }

        private static object? ReadPrivate(object target, string name)
            => (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(target.GetType().FullName, name)).GetValue(target);

        internal async Task WaitForCompletion(CancellationToken token)
        {
            Ensure(_physical is not null, "server Dispose must publish its original cleanup source");
            _bodyWaiter = _physical!.WaitAsync(token);
            _bodyWaiterOutcome = ReadOutcome(_bodyWaiter);
            await _bodyWaiter;
        }

        private static Exception Sole(Task task)
        {
            Ensure(task.IsFaulted && task.Exception is { InnerExceptions.Count: 1 },
                "expected exactly one original Task fault, not cancellation or unknown siblings");
            return task.Exception!.InnerExceptions[0];
        }

        private static void SameFault(Task source, Exception expected)
            => Ensure(ReferenceEquals(Sole(source), expected), "original source retained another failure shape");

        internal void AssertExpectedCleanup()
        {
            Ensure(_captured && _physical is { IsCompletedSuccessfully: true }, "physical outcomes must be actually joined");
            var server = Server ?? throw new Exception("missing concrete server owner");
            var module = Module ?? throw new Exception("missing returned dynamic module");
            var dynamicService = DynamicService ?? throw new Exception("missing dynamic service receipt");
            var staticService = StaticService ?? throw new Exception("missing static service receipt");
            lock (ReceiptGate)
            {
                Ensure(dynamicService.Sources.Count == 1 && staticService.Sources.Count == 1 &&
                    CallbackOrder.Count == 2 && !CallbackOrder[0] && CallbackOrder[1],
                    "server cleanup must dispose dynamic then framework-owned static service exactly once");
                SameFault(dynamicService.Sources[0], dynamicService.Failure);
                SameFault(staticService.Sources[0], staticService.Failure);
            }
            var stopFailure = Sole(_dispose!);
            Ensure(stopFailure is PhysicalTaskFailureException { RawException: AggregateException },
                "strict Stop must retain the physical carrier and complete raw graph");
            var pair = (AggregateException)((PhysicalTaskFailureException)stopFailure).RawException;
            Ensure(pair.InnerExceptions.Count == 2 && ReferenceEquals(pair.InnerExceptions[0], dynamicService.Failure) &&
                ReferenceEquals(pair.InnerExceptions[1], staticService.Failure),
                "server cleanup must retain exactly its ordered dynamic and static failures");
            var lifecycle = server.LifecycleForDiagnostics;
            lock (lifecycle.StateGate)
            {
                Ensure(ReferenceEquals(ReadPrivate(lifecycle, "_stopTask"), _dispose), "Dispose must retain the actual shared Stop source");
                Ensure(ReadPrivate(lifecycle, "_startTask") is null && ReadPrivate(lifecycle, "_acceptTask") is null,
                    "selected fixture must remain never-started");
                var strict = lifecycle.StrictPhysicalDrainForDiagnostics;
                Ensure(!strict.Requested && strict.Drain is { IsCompletedSuccessfully: true } && strict.Failure is null,
                    "Stop-origin retirement must not invent a second supervisor failure");
                Ensure(lifecycle.ShutdownCleanupObserverTaskForDiagnostics is null &&
                    ReadPrivate(lifecycle, "_serviceCleanupObserver") is null && lifecycle.TerminalFailureObserverTaskForDiagnostics is null,
                    "expected cleanup has no deferred or terminal observer owners");
                SameFault(lifecycle.DeferredServiceCleanupTaskForDiagnostics!, stopFailure);
                SameFault(server.WaitForShutdownAsync(), stopFailure);
                Ensure(ReferenceEquals(lifecycle.StrictPhysicalTerminalFailureForDiagnostics, stopFailure),
                    "terminal lifecycle must preserve this exact Stop failure");
            }
            var registry = (ServerServiceModuleRegistry)GetPrivateField(server, "_serviceModuleRegistry");
            lock (registry.Gate)
            {
                Ensure(module.State == SharpLinkDynamicModuleState.Released &&
                    !registry.DynamicModules.ContainsKey(ModuleAssembly!) && !registry.UnregisterOperations.ContainsKey(ModuleAssembly!),
                    "exact dynamic module and retirement entry must really release");
                Ensure(module.RetirementRegistration is null, "Stop-origin operation has no supervisor receipt");
                Ensure(Sole(module.RetirementOperation!) is PhysicalTaskFailureException raw &&
                    ReferenceEquals(raw.RawException, dynamicService.Failure), "retirement keeps sole original dynamic failure");
                Ensure(module.RetirementTracking is { IsCompletedSuccessfully: true } && module.RetirementTracking.Result is null,
                    "retirement tracking must succeed without a hidden failure");
                Ensure(module.RetirementObservation is { IsCompletedSuccessfully: true } &&
                    module.RetirementObservation.Result.Failure is null && !module.RetirementObservation.Result.SupervisorOwned,
                    "retirement observation must be joined and not supervisor-owned");
                Ensure(module.CleanupCompletion?.Task is { IsCompletedSuccessfully: true } cleanup &&
                    ReferenceEquals(cleanup.Result, dynamicService.Failure), "cleanup receipt is successful outcome data containing exact D");
                Ensure(module.RetirementControl?.Driver.Task is { IsCompletedSuccessfully: true } driver &&
                    !driver.Result.HasRetainedTimer && driver.Result.PrimaryFailure is null &&
                    driver.Result.CancellationFailure is null && driver.Result.TimerFailure is null && driver.Result.WakeFailure is null,
                    "actual retirement driver must end without timer or independent cleanup failure");
            }
            Ensure(server.LifecycleState == SharpLinkServerLifecycleState.Faulted,
                "two intentional cleanup failures leave the real server Faulted, never falsely Stopped");
            Ensure(ReadPrivate(GetPrivateField(server, "_runtimeContext"), "_metadataOwner") is null,
                "runtime cleanup must reach its final metadata release");
            AssertListener();
            _accepted = true; // only exact source stages above, never an exception filter
        }

        private void AssertListener()
        {
            Ensure(Listener is not null, "missing selected listener");
            lock (Listener!.Sources)
                Ensure(Listener.Sources.Count == 1 && Listener.Sources[0].IsCompletedSuccessfully &&
                    Listener.Outcomes.Count == 1 && Listener.Outcomes[0].IsCompletedSuccessfully &&
                    Listener.Outcomes[0].Result is null && Listener.InvocationFailures.Count == 0,
                    "listener's one actual disposal must succeed");
        }

        internal async Task FinishAsync(CancellationToken token)
        {
            BeginDispose();
            if (_physical is not null)
            {
                _finishWaiter = _physical.WaitAsync(token);
                _finishWaiterOutcome = ReadOutcome(_finishWaiter);
                try { await _finishWaiter; }
                catch (Exception failure) { Failures.Add(failure); }
            }
            if (!BuildAttempted)
            {
                if (StaticService is not null)
                    try { await StaticService.DisposeAsync(); } catch (Exception failure) { Failures.Add(failure); }
                if (Listener is not null)
                    try { await Listener.DisposeAsync(); } catch (Exception failure) { Failures.Add(failure); }
            }
            if (!DynamicOffered && DynamicService is not null && DynamicRegistration is null)
                try { await DynamicService.DisposeAsync(); } catch (Exception failure) { Failures.Add(failure); }
            if (!DynamicOffered && DynamicRegistration is not null)
            {
                try
                {
                    _unofferedRegistration = DynamicRegistration.DisposeAsync().AsTask();
                    _unofferedRegistrationOutcome = ReadOutcome(_unofferedRegistration);
                    await _unofferedRegistration.WaitAsync(token);
                }
                catch (Exception failure) { Failures.Add(failure); }
            }
            // Latch before the full outcome sweep: a later completion cannot
            // upgrade an uncertain transfer or missing original owner to release.
            var ended = !BuildAttempted || (Endpoint is not null && Server is not null &&
                _captured && _dispose is { IsCompleted: true } && _physical is { IsCompletedSuccessfully: true } &&
                (!DynamicOffered || Module is not null));
            foreach (var source in _lifecycle) ended &= source.Source.IsCompleted && source.Outcome.IsCompletedSuccessfully;
            foreach (var source in _retirement) ended &= source.Source.IsCompleted && source.Outcome.IsCompletedSuccessfully;
            if (DynamicRegistration is not null && !DynamicOffered)
                ended &= _unofferedRegistration is { IsCompleted: true } && _unofferedRegistrationOutcome is { IsCompletedSuccessfully: true };
            if (!_accepted && Endpoint is not null && ended)
            {
                try { AssertExpectedCleanup(); }
                catch (Exception failure) { Failures.Add(failure); ended = false; }
            }
            if (BuildAttempted) ended &= _accepted; // unknown Build/transfer or unrecognized fault stays retained
            Append(_bodyWaiterOutcome); Append(_finishWaiterOutcome); Append(_physicalOutcome);
            if (!_accepted)
            {
                Append(_disposeOutcome);
                foreach (var source in _lifecycle) Append(source.Outcome);
                foreach (var source in _retirement) Append(source.Outcome);
                lock (ReceiptGate)
                {
                    if (StaticService is not null) Failures.AddRange(StaticService.Outcomes);
                    if (DynamicService is not null) Failures.AddRange(DynamicService.Outcomes);
                }
            }
            Append(_unofferedRegistrationOutcome);
            if (Listener is not null)
                lock (Listener.Sources)
                {
                    foreach (var outcome in Listener.Outcomes) { ended &= outcome.IsCompletedSuccessfully; Append(outcome); }
                    foreach (var source in Listener.Sources) ended &= source.IsCompletedSuccessfully;
                    Failures.AddRange(Listener.InvocationFailures);
                    ended &= Listener.InvocationFailures.Count == 0 && Listener.Sources.Count == 1;
                }
            if (!ended)
            {
                Failures.Add(new InvalidOperationException("registered cleanup retains unknown or unfinished owners and isolation"));
                lock (Retained) Retained.Add(this);
                return;
            }
            try
            {
                if (CatalogAttempted && Manifest is not null) RollbackTestIsolation.RemoveManifestFromCatalog(Manifest);
                RollbackState.TestIsolation.Release();
            }
            catch (Exception failure)
            {
                Failures.Add(failure);
                lock (Retained) Retained.Add(this);
            }
        }

        private void Append(Task<Exception?>? outcome)
        {
            if (outcome is { IsCompletedSuccessfully: true } && outcome.Result is { } failure) Failures.Add(failure);
        }

        internal void ThrowIfFailed()
        {
            if (Failures.Count != 0) throw new AggregateException(Failures);
        }
    }


}
