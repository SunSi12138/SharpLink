using System.IO.Pipelines;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

// Real current-ABI owners, registry publication, singleton disposal and adapter scopes.
// Read-only reflection captures actual registered owners; it never changes registry membership.
[NotInParallel]
public sealed class DependencyRetirementOwnershipTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static Fixture? Current;

    [Test]
    public async Task DependantRemainsVisibleThroughServiceThenAdapterCleanup()
    {
        var f = new Fixture(); Exception? body = null;
        try
        {
            f.Initialize();
            f.ReleaseInvocation();
            f.Retirement = f.Server!.UnregisterAssemblyAsync(f.A!, TimeSpan.Zero).AsTask();
            await f.ServiceEntered.Task.WaitAsync(Bound);
            var repeated = f.Server!.UnregisterAssemblyAsync(f.A!, TimeSpan.Zero).AsTask();
            Ensure(!f.Retirement.IsCompleted && !repeated.IsCompleted, "both callers join actual service cleanup");
            Ensure(Capture(() => f.Server!.UnregisterAssemblyAsync(f.B!, TimeSpan.Zero)) is InvalidOperationException,
                "B remains dependency-protected while A service Dispose is active");
            Ensure(!f.Server!.RegisterAssembly(f.A!).Succeeded, "same Assembly cannot be republished during its full retirement");
            f.ServiceRelease.TrySetResult();
            await f.AdapterEntered.Task.WaitAsync(Bound);
            Ensure(f.ServiceDisposals == 1 && f.BDisposals == 0 && !repeated.IsCompleted,
                "service exited but adapter still retains dependency graph");
            Ensure(Capture(() => f.Server!.UnregisterAssemblyAsync(f.B!, TimeSpan.Zero)) is InvalidOperationException,
                "B remains protected at adapter stage too");
            f.AdapterRelease.Set();
            Ensure((await f.Retirement.WaitAsync(Bound)).ReferencesReleased &&
                (await repeated.WaitAsync(Bound)).ReferencesReleased, "all exact joiners observe final release");
            Ensure(f.Module!.State == SharpLinkDynamicModuleState.Released && f.Module!.CleanupCompletion!.Task.IsCompletedSuccessfully,
                "released state and retained raw cleanup outcome are distinct final facts");
            Ensure((await f.Server!.UnregisterAssemblyAsync(f.B!, TimeSpan.Zero)).ReferencesReleased && f.BDisposals == 1,
                "B cleanup becomes legal only after A physical retirement");
        }
        catch (Exception error) { body = error; }
        await f.Cleanup(body);
    }

    [Test]
    public async Task ZeroGraceAndOriginalServerStopBudgetDoNotAdvancePastHeldDependant()
    {
        var f = new Fixture(); Exception? body = null;
        try
        {
            f.Initialize();
            var grace = await f.Server!.UnregisterAssemblyAsync(f.A!, TimeSpan.Zero).AsTask().WaitAsync(Bound);
            Ensure(!grace.ReferencesReleased && f.Module!.RemainingCalls == 1, "zero grace is only a waiter result");
            f.ReleaseInvocation();
            await f.ServiceEntered.Task.WaitAsync(Bound);
            f.Stop = f.Server!.StopAsync(TimeSpan.Zero).AsTask();
            await Until(() => f.Clock.ActiveTimerCount != 0, "original Stop cleanup timer");
            f.Clock.Advance(ServerShutdownPlan.Default.CleanupBudget);
            f.ExpectedStopFailure = await Observe(f.Stop);
            Ensure(f.ExpectedStopFailure is not null && f.Server!.LifecycleState == SharpLinkServerLifecycleState.Faulted,
                "original absolute Stop budget returns without pretending physical release");
            var lifecycle = Read<SharpLinkServer.ServerLifecycleCoordinator>(f.Server!, "_lifecycle");
            var physical = lifecycle.DeferredServiceCleanupTaskForDiagnostics;
            Ensure(physical is not null && !physical.IsCompleted && f.BDisposals == 0,
                "actual retained ordered cleanup is still at A, not B or Runtime");
            f.ServiceRelease.TrySetResult();
            await f.AdapterEntered.Task.WaitAsync(Bound);
            Ensure(!physical!.IsCompleted && f.BDisposals == 0, "same physical owner spans A adapter callback");
            f.AdapterRelease.Set();
            await physical.WaitAsync(Bound);
            if (lifecycle.ShutdownCleanupObserverTaskForDiagnostics is { } observer) await observer.WaitAsync(Bound);
            Ensure(f.BDisposals == 1 && f.Module!.State == SharpLinkDynamicModuleState.Released,
                "B and Runtime sequence only advances after both actual A callbacks exit");
        }
        catch (Exception error) { body = error; }
        await f.Cleanup(body);
    }

    [Test]
    public async Task DeclaredDependencyPinProtectsFactoryAndRollbackButCannotPublishAfterDraining()
    {
        var f = new Fixture(); Exception? body = null;
        try
        {
            f.Initialize(registerA: false);
            f.HoldFactory = true;
            var registration = Task.Run(() => f.Server!.RegisterAssembly(f.A!));
            f.Additional = registration;
            await f.FactoryEntered.Task.WaitAsync(Bound);
            var zeroGrace = await f.Server!.UnregisterAssemblyAsync(f.B!, TimeSpan.Zero).AsTask().WaitAsync(Bound);
            Ensure(!zeroGrace.ReferencesReleased && f.BDisposals == 0,
                "actual preparation pin prevents B cleanup while A adapter factory is running");
            f.FactoryRelease.Set();
            await f.AdapterEntered.Task.WaitAsync(Bound);
            Ensure(!registration.IsCompleted && f.BDisposals == 0,
                "final Running revalidation rejected A; its rollback callback still owns B pin");
            f.AdapterRelease.Set();
            Ensure(!(await registration.WaitAsync(Bound)).Succeeded, "Draining exact dependency cannot be published");
            Ensure((await f.Server!.UnregisterAssemblyAsync(f.B!, TimeSpan.Zero).AsTask().WaitAsync(Bound)).ReferencesReleased,
                "B becomes releasable only after actual rollback exit");
            Ensure(f.BDisposals == 1 && f.ServiceDisposals == 0, "no service activated and B cleaned once");
        }
        catch (Exception error) { body = error; }
        await f.Cleanup(body);
    }

    [Test]
    public async Task ServiceRawCancellationAndAdapterFailureKeepIndependentPhysicalOutcomes()
    {
        var f = new Fixture(); Exception? body = null;
        var serviceFailure = new OperationCanceledException("service physical failure");
        var adapterFailure = new IOException("adapter physical failure");
        try
        {
            f.Initialize();
            f.ServiceFailure = serviceFailure; f.AdapterFailure = adapterFailure;
            f.ServiceProbe = () =>
            {
                Ensure(Capture(() => f.Server!.StopAsync(TimeSpan.Zero)) is ColdMetadataException,
                    "service callback cannot Stop its owning domain");
                Ensure(Capture(() => f.Server!.UnregisterAssemblyAsync(f.A!, TimeSpan.Zero)) is ColdMetadataException,
                    "service callback cannot join its own retirement");
                Read<SharpLinkRuntimeContext>(f.Server!, "_runtimeContext").Codecs.GetCodec<ValueB>();
            };
            f.ServiceRelease.TrySetResult(); f.AdapterRelease.Set(); f.ReleaseInvocation();
            f.Retirement = f.Server!.UnregisterAssemblyAsync(f.A!, TimeSpan.Zero).AsTask();
            f.ExpectedRetirementFailure = await Observe(f.Retirement);
            Ensure(HasOnly(f.ExpectedRetirementFailure, serviceFailure, adapterFailure),
                "public exact join preserves raw OCE plus independent adapter failure, no cancellation suppression");
            Ensure(f.Module!.State == SharpLinkDynamicModuleState.Released && f.ServiceDisposals == 1 && f.ADisposals == 1,
                "all actual callback obligations exit once despite independent errors");
            await f.Server!.UnregisterAssemblyAsync(f.B!, TimeSpan.Zero).AsTask().WaitAsync(Bound);
            var supervisor = Read<FrameworkTaskSupervisor>(f.Server!, "_frameworkTasks");
            supervisor.Seal();
            var strictFailure = await Observe(supervisor.DrainAsync());
            Ensure(HasOnly(strictFailure, serviceFailure, adapterFailure), "strict owner reports the same two actual stages");
        }
        catch (Exception error) { body = error; }
        await f.Cleanup(body);
    }

    [Test]
    public async Task EarlyDeferredCleanupFailureSurvivesLaterLogicalStopFinalization()
    {
        var f = new Fixture(); Exception? body = null; ServerConnectionState? connection = null;
        var acquired = false;
        var expected = new OperationCanceledException("early deferred service failure");
        try
        {
            f.Initialize(); f.ReleaseInvocation();
            f.HoldListener = true; f.ServiceFailure = expected;
            f.ServiceRelease.TrySetResult(); f.AdapterRelease.Set();
            var input = new Pipe(); var output = new Pipe();
            var session = RpcSessionTestFixture.CreateSessionOverTestTransport(Guid.NewGuid().ToString("N"),
                input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
            connection = new ServerConnectionState(session, new RpcSessionGeneratedServerBridge(session),
                new StripedLongMap<ServerCallCancellationState>(RpcSessionTestFixture.RuntimeContext.Concurrency),
                CancellationToken.None, RpcSessionTestFixture.RuntimeContext.TimeProvider);
            Ensure(connection.MarkReady(null), "actual admission connection ready");
            Ensure(f.Server!.TryAcquireCall(connection) == ServerCallAdmissionResult.Acquired, "actual Server call admission");
            acquired = true;
            f.Stop = f.Server!.StopAsync(TimeSpan.Zero).AsTask();
            await f.ListenerEntered.Task.WaitAsync(Bound);
            f.Server!.ReleaseCall(connection); acquired = false;
            var lifecycle = Read<SharpLinkServer.ServerLifecycleCoordinator>(f.Server!, "_lifecycle");
            var physical = lifecycle.DeferredServiceCleanupTaskForDiagnostics!;
            f.ExpectedPhysicalFailure = await Observe(physical);
            Ensure(HasOnly(f.ExpectedPhysicalFailure, expected), "actual deferred physical task preserves raw service failure");
            await Read<Task>(lifecycle, "_serviceCleanupObserver").WaitAsync(Bound);
            Ensure(!f.Stop.IsCompleted, "listener keeps final logical Stop publication later than cleanup observer");
            f.ListenerRelease.TrySetResult();
            f.ExpectedStopFailure = await Observe(f.Stop);
            Ensure(HasOnly(f.ExpectedStopFailure, expected), "Stop merge cannot overwrite the early independent raw failure");
        }
        catch (Exception error) { body = error; }
        finally
        {
            f.ListenerRelease.TrySetResult();
            if (connection is not null)
            {
                try { if (acquired) f.Server!.ReleaseCall(connection); }
                catch (Exception error) { body = body is null ? error : new AggregateException(body, error); }
                try { await connection.CloseAsync(); await connection.ServiceCleanupTask.WaitAsync(Bound); }
                catch (Exception error) { body = body is null ? error : new AggregateException(body, error); }
            }
        }
        await f.Cleanup(body);
    }

    private sealed class Fixture
    {
        internal readonly ManualTimeProvider Clock = new();
        internal readonly TaskCompletionSource FactoryEntered = Signal();
        internal readonly ManualResetEventSlim FactoryRelease = new();
        internal bool HoldFactory, HoldListener;
        internal readonly TaskCompletionSource ListenerEntered = Signal();
        internal readonly TaskCompletionSource ListenerRelease = Signal();
        internal Task? Additional;
        internal readonly TaskCompletionSource ServiceEntered = Signal();
        internal readonly TaskCompletionSource ServiceRelease = Signal();
        internal readonly TaskCompletionSource AdapterEntered = Signal();
        internal readonly ManualResetEventSlim AdapterRelease = new();
        internal int ServiceDisposals, ADisposals, BDisposals;
        internal SharpLinkServer? Server;
        internal Assembly? A, B;
        internal SharpLinkDynamicModule? Module;
        internal Task<SharpLinkAssemblyUnregisterResult>? Retirement;
        internal Task? Stop;
        internal Exception? ExpectedStopFailure, ExpectedRetirementFailure, ExpectedPhysicalFailure, ServiceFailure, AdapterFailure;
        internal Action? ServiceProbe;
        private readonly OwnerContext _context = new();
        private SharpLinkDynamicModuleLease _invocation;
        private bool _holdsInvocation;

        internal void Initialize(bool registerA = true)
        {
            Current = this;
            B = Emit(_context, 2, null);
            A = Emit(_context, 1, B.FullName);
            Server = (SharpLinkServer)SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .UseTimeProvider(Clock).UseTransport(new Listener()).Build();
            Ensure(Server.RegisterAssembly(B).Succeeded, "B actual manifest registration");
            if (!registerA) return;
            Ensure(Server.RegisterAssembly(A).Succeeded, "A actual manifest/dependency/service registration");
            var registry = Read<ServerServiceModuleRegistry>(Server, "_serviceModuleRegistry");
            ServiceRegistration service;
            lock (registry.Gate)
            {
                using var snapshot = registry.CaptureServices();
                service = snapshot.Services[1];
                Module = service.Module;
            }
            Ensure(service.TryAcquireDynamicSingleton(false, out _, out _invocation), "actual singleton activation and module lease");
            _holdsInvocation = true;
        }
        internal void ReleaseInvocation()
        { if (_holdsInvocation) { _holdsInvocation = false; _invocation.Dispose(); } }
        internal async Task Cleanup(Exception? body)
        {
            List<Exception> failures = [];
            if (body is not null) failures.Add(body);
            ListenerRelease.TrySetResult(); FactoryRelease.Set(); ServiceRelease.TrySetResult(); AdapterRelease.Set();
            try { ReleaseInvocation(); } catch (Exception error) { failures.Add(error); }
            if (Additional is not null && await Observe(Additional) is { } additionalFailure) failures.Add(additionalFailure);
            if (Retirement is not null && await Observe(Retirement) is { } retirementFailure &&
                !ReferenceEquals(retirementFailure, ExpectedRetirementFailure)) failures.Add(retirementFailure);
            if (Server is not null)
            {
                // Advancing only during exceptional cleanup cannot erase the body's first failure.
                var dispose = Server.DisposeAsync().AsTask();
                Clock.Advance(TimeSpan.FromSeconds(30));
                var error = await Observe(dispose);
                if (error is not null && !ReferenceEquals(error, ExpectedStopFailure)) failures.Add(error);
                var lifecycle = Read<SharpLinkServer.ServerLifecycleCoordinator>(Server, "_lifecycle");
                if (lifecycle.DeferredServiceCleanupTaskForDiagnostics is { } physical && await Observe(physical) is { } physicalFailure &&
                    !ReferenceEquals(physicalFailure, ExpectedPhysicalFailure)) failures.Add(physicalFailure);
                if (lifecycle.ShutdownCleanupObserverTaskForDiagnostics is { } observer && await Observe(observer) is { } observerFailure)
                    failures.Add(observerFailure);
            }
            // A bounded wait failure is not a physical join. Keep fixture roots/barriers alive
            // if any actual owner remains; never replace its task with a completed sentinel.
            var lifecycleOwner = Server is null ? null : Read<SharpLinkServer.ServerLifecycleCoordinator>(Server, "_lifecycle");
            var physicalOwner = lifecycleOwner?.DeferredServiceCleanupTaskForDiagnostics;
            var shutdownObserver = lifecycleOwner?.ShutdownCleanupObserverTaskForDiagnostics;
            var registryWorkersExited = true;
            if (Server is not null)
            {
                var registry = Read<ServerServiceModuleRegistry>(Server, "_serviceModuleRegistry");
                lock (registry.Gate)
                    registryWorkersExited = registry.UnregisterOperations.Entries.All(pair => pair.Value.Task.IsCompleted &&
                        (pair.Value.Module.RetirementObservation?.IsCompleted ?? false));
            }
            if ((Additional?.IsCompleted ?? true) && (Retirement?.IsCompleted ?? true) &&
                (physicalOwner?.IsCompleted ?? true) && (shutdownObserver?.IsCompleted ?? true) && registryWorkersExited &&
                (Server?.FrameworkTaskSnapshotForDiagnostics.ActiveTasks ?? 0) == 0)
            { if (ReferenceEquals(Current, this)) Current = null; FactoryRelease.Dispose(); AdapterRelease.Dispose(); _context.Unload(); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
    }

    public class ServiceBase : IAsyncDisposable
    {
        private readonly Fixture _fixture = Current!;
        public async ValueTask DisposeAsync()
        {
            var f = _fixture;
            Interlocked.Increment(ref f.ServiceDisposals); f.ServiceEntered.TrySetResult();
            await f.ServiceRelease.Task.ConfigureAwait(false);
            f.ServiceProbe?.Invoke();
            if (f.ServiceFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(f.ServiceFailure).Throw();
        }
    }

    public class ManifestBase : ISharpLinkGeneratedAssemblyManifest
    {
        private readonly int _kind;
        public ManifestBase(int kind, string dependency)
        {
            _kind = kind;
            Dependencies = dependency.Length == 0 ? [] : [dependency];
            if (kind == 1)
            {
                var contract = OwnerAssembly.GetType("OwnedContract", true)!;
                var implementation = OwnerAssembly.GetType("OwnedService", true)!;
                Contracts = [new(contract, contract.FullName!, 1, new string('a', 64), [],
                    static (_, _) => throw new NotSupportedException(), static _ => new Stub())];
                Services = [new(contract, implementation, contract.FullName!, implementation.FullName!, 1,
                    new string('b', 64), SharpLinkServiceLifetime.Singleton, [], _ => Activator.CreateInstance(implementation)!)];
            }
            Codecs = kind == 1 ? [new Factory<ValueA>(new Adapter(kind))] : [new Factory<ValueB>(new Adapter(kind))];
        }
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "dependency-retirement-control";
        public Assembly OwnerAssembly => GetType().Assembly;
        public RpcHash128 RpcAssemblyHash => new((ulong)_kind, 0x775);
        public string CompileTimeDescriptor => "dependency-retirement-control";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } = [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; } = [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; }
        public IReadOnlyList<string> Dependencies { get; }
    }
    private sealed class Stub : IRpcStub
    {
        public long InterfaceHash => 1;
        public RpcMethodFacts ResolveMethodFacts(long id) => RpcMethodFacts.UnknownMethod;
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method, long id, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method, long id, ReadOnlySequence<byte> args, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method, long id, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => ValueTask.CompletedTask;
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method, long id, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken ct) => ValueTask.CompletedTask;
    }
    private sealed class Adapter(int kind) : IRpcCodecAdapter, IRpcCodecAdapterScope
    {
        private readonly Fixture _fixture = Current!;
        public string AdapterId => "test.dependency-retirement/" + kind;
        public IRpcCodecAdapterScope CreateScope()
        {
            var f = _fixture;
            if (kind == 1 && f.HoldFactory)
            { f.FactoryEntered.TrySetResult(); if (!f.FactoryRelease.Wait(Bound)) throw new TimeoutException("A factory release"); }
            return this;
        }
        public IRpcCodec<T> CreateCodec<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() => new Codec<T>();
        public void Dispose()
        {
            var f = _fixture;
            if (kind == 2) { Interlocked.Increment(ref f.BDisposals); return; }
            Interlocked.Increment(ref f.ADisposals); f.AdapterEntered.TrySetResult();
            if (!f.AdapterRelease.Wait(Bound)) throw new TimeoutException("A adapter cleanup release");
            if (f.AdapterFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(f.AdapterFailure).Throw();
        }
    }
    private sealed class ValueA;
    private sealed class ValueB;
    private sealed class Codec<T> : IRpcCodec<T>
    {
        public void Serialize(in T value, IBufferWriter<byte> writer) { }
        public T? Deserialize(in ReadOnlySequence<byte> bytes) => default;
    }
    private sealed class Factory<T>(Adapter adapter) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public RpcHash128 CodecHash => new(1, 775);
        public string AdapterId => adapter.AdapterId;
        public IRpcCodecAdapter Adapter => adapter;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? scope) => new Codec<T>();
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
    }
    private sealed class Listener : IServerTransportListener
    {
        private readonly Fixture _fixture = Current!;
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken ct = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync()
        {
            var f = _fixture;
            if (!f.HoldListener) return ValueTask.CompletedTask;
            f.ListenerEntered.TrySetResult(); return new ValueTask(f.ListenerRelease.Task);
        }
    }
    private sealed class OwnerContext() : AssemblyLoadContext("dependency-" + Guid.NewGuid().ToString("N"), true)
    {
        protected override Assembly? Load(AssemblyName name)
            => Assemblies.Concat(Default.Assemblies).FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(name, a.GetName()));
    }
    private static Assembly Emit(OwnerContext context, int kind, string? dependency)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName("DependencyOwner" + kind + Guid.NewGuid().ToString("N")), typeof(object).Assembly);
        var module = builder.DefineDynamicModule("Owner");
        if (kind == 1)
        {
            var contract = module.DefineType("OwnedContract", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
            contract.AddInterfaceImplementation(typeof(IService)); var contractType = contract.CreateType();
            var service = module.DefineType("OwnedService", TypeAttributes.Public | TypeAttributes.Sealed, typeof(ServiceBase), [contractType]);
            service.DefineDefaultConstructor(MethodAttributes.Public); service.CreateType();
        }
        var manifest = module.DefineType("OwnedManifest", TypeAttributes.Public | TypeAttributes.Sealed, typeof(ManifestBase));
        var constructor = manifest.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var il = constructor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, kind); il.Emit(OpCodes.Ldstr, dependency ?? "");
        il.Emit(OpCodes.Call, typeof(ManifestBase).GetConstructor([typeof(int), typeof(string)])!); il.Emit(OpCodes.Ret);
        var type = manifest.CreateType();
        builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(SharpLinkGeneratedAssemblyManifestAttribute).GetConstructor(
            [typeof(Type), typeof(int), typeof(int), typeof(string), typeof(string)])!,
            [type, SharpLinkGeneratedManifestVersions.Api, SharpLinkGeneratedManifestVersions.Protocol, "dependency-retirement-control", SharpLinkGeneratedManifestVersions.AbiIdentity]));
        using var bytes = new MemoryStream(); builder.Save(bytes); bytes.Position = 0; return context.LoadFromStream(bytes);
    }
    private static bool HasOnly(Exception? actual, params Exception[] expected)
    {
        List<Exception> leaves = [];
        void Visit(Exception error)
        {
            if (error is PhysicalTaskFailureException marker) Visit(marker.RawException);
            else if (error is AggregateException aggregate) foreach (var child in aggregate.InnerExceptions) Visit(child);
            else leaves.Add(error);
        }
        if (actual is null) return false;
        Visit(actual);
        return leaves.Count == expected.Length && expected.All(item => leaves.Count(leaf => ReferenceEquals(leaf, item)) == 1);
    }
    private static T Read<T>(object owner, string field) => (T)(owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static async Task<Exception?> Observe(Task task) { try { await task.WaitAsync(Bound); return null; } catch (Exception error) { return error; } }
    private static async Task Until(Func<bool> condition, string label)
    { var elapsed = Stopwatch.StartNew(); while (!condition()) { if (elapsed.Elapsed > Bound) throw new TimeoutException(label); await Task.Delay(1); } }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
