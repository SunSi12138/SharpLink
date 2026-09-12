using System.Collections.Generic;
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

public class ServiceRegistrationTests
{
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
    public async Task RegisteredServiceCleanupShouldPreserveDynamicAndFrameworkOwnedStaticFailures()
    {
        await RollbackState.TestIsolation.WaitAsync();
        var manifest = new StaticCleanupManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);
        SharpLinkServer? server = null;
        var stopAttempted = false;
        try
        {
            var staticService = new ThrowingStaticCleanupService("static ownership cleanup failed");
            var builder = SharpLinkServerBuilder.Create()
                .UseTransport(new NoopListener())
                .DisableAutomaticServiceRegistration()
                .UseServiceProvider(new EmptyServiceProvider())
                .ReplaceService<IStaticCleanupContract>(staticService);
            MarkReplacementFrameworkOwned(builder, typeof(IStaticCleanupContract));
            server = (SharpLinkServer)builder.Build();
            AddDynamicModule(server, "dynamic-ownership",
                CreateThrowingRegistration(typeof(string), "dynamic ownership cleanup failed"));

            stopAttempted = true;
            var failure = await CaptureAsync(server.DisposeAsync);

            Ensure(ContainsMessage(failure, "dynamic ownership cleanup failed"),
                "server cleanup must retain its dynamic-module failure");
            Ensure(ContainsMessage(failure, "static ownership cleanup failed"),
                "server cleanup must retain its framework-owned static-service failure");
            Ensure(staticService.DisposeCount == 1,
                "server cleanup must dispose the framework-owned static replacement exactly once");
        }
        finally
        {
            if (!stopAttempted && server is not null)
            {
                try
                {
                    await server.DisposeAsync();
                }
                catch
                {
                }
            }
            RollbackTestIsolation.RemoveManifestFromCatalog(manifest);
            RollbackState.TestIsolation.Release();
            GC.KeepAlive(manifest);
        }
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
        var codecRegistration = runtime.PrepareGeneratedManifest(manifest);
        runtime.AdoptGeneratedManifest(codecRegistration);
        var module = new SharpLinkDynamicModule(assembly, manifest, codecRegistration);

        var registry = (ServerServiceModuleRegistry)GetPrivateField(server, "_serviceModuleRegistry");
        lock (registry.Gate)
        {
            registry.DynamicModules.Add(assembly, module);
            registry.DetachedModuleServices.Add(module, registrations);
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
                static _ => new StubMarker())
        ];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class StubMarker : IRpcStub
    {
        public long InterfaceHash => 1;

        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output)
            => ValueTask.CompletedTask;

        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
