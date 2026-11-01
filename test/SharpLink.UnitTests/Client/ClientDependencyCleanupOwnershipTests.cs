using System.Linq;
using SharpLink.Sdk;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

// Actual current-ABI owners and real module/codec scope cleanup. No registry mutation seam.
public sealed class ClientDependencyCleanupOwnershipTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Test]
    public async Task HeldAdapterKeepsDependencyVisibleAndRepeatedUnregisterJoins()
    {
        var fixture = new Fixture();
        Exception? primary = null;
        Task<SharpLinkAssemblyUnregisterResult>? first = null;
        Task<SharpLinkAssemblyUnregisterResult>? repeated = null;
        try
        {
            fixture.AScope.Hold = true;
            fixture.AScope.DuringDispose = () =>
            {
                RequireReentry(Capture(() => fixture.Client.UnregisterAssemblyAsync(fixture.A, TimeSpan.Zero)));
                RequireReentry(Capture(() => fixture.Client.StopAsync()));
                ((ITrackedUse)fixture.BScope.Codec!).Use();
            };
            first = fixture.Client.UnregisterAssemblyAsync(fixture.A, TimeSpan.Zero).AsTask();
            await fixture.AScope.Entered.Task.WaitAsync(Bound);
            Ensure(fixture.AModule.State != SharpLinkDynamicModuleState.Released && fixture.BScope.Disposals == 0,
                "actual adapter entry is not physical module release");
            var dependencyError = Capture(() => fixture.Client.UnregisterAssemblyAsync(fixture.B, TimeSpan.Zero));
            Ensure(dependencyError is InvalidOperationException && dependencyError.Message.Contains("depends on it", StringComparison.Ordinal),
                "B unregister must reject the actual still-cleaning A dependency");
            repeated = fixture.Client.UnregisterAssemblyAsync(fixture.A, TimeSpan.Zero).AsTask();
            Ensure(!first.IsCompleted && !repeated.IsCompleted && fixture.AScope.Disposals == 1,
                "both callers join one actual callback, not module drain alone");
            fixture.AScope.Release.Set();
            Ensure((await first.WaitAsync(Bound)).ReferencesReleased && (await repeated.WaitAsync(Bound)).ReferencesReleased,
                "both waiters see the same completed physical release");
            Ensure(fixture.BScope.Uses == 1 && fixture.AModule.State == SharpLinkDynamicModuleState.Released,
                "the exact B codec remains usable through A adapter cleanup");
            Ensure((await fixture.Client.UnregisterAssemblyAsync(fixture.B, TimeSpan.Zero)).ReferencesReleased,
                "B may retire only after A cleanup exits");
            Ensure(fixture.AScope.Disposals == 1 && fixture.BScope.Disposals == 1,
                "each original adapter scope is disposed exactly once");
            Ensure(fixture.Runtime.Authority.Bytes == fixture.BaselineBytes,
                "dynamic owner/map metadata refunds to the actual empty-runtime baseline");
        }
        catch (Exception exception) { primary = exception; throw; }
        finally { await fixture.CleanupAsync(primary, first, repeated); }
    }

    [Test]
    public async Task TimedOutAndCanceledWaitersDoNotLetStopOvertakeDependentCleanup()
    {
        var fixture = new Fixture();
        Exception? primary = null;
        SharpLinkDynamicModuleLease lease = default;
        Task? stop = null;
        try
        {
            fixture.AScope.Hold = true;
            Ensure(fixture.AModule.TryAcquire(stream: false, out lease), "actual module lease acquired");
            var timedOut = await fixture.Client.UnregisterAssemblyAsync(fixture.A, TimeSpan.Zero);
            Ensure(!timedOut.ReferencesReleased && fixture.AScope.Disposals == 0,
                "public zero-grace result must not pretend the held module drained");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var canceled = await Failure(async () => await fixture.Client.UnregisterAssemblyAsync(
                fixture.A, Bound, cancellation.Token));
            Ensure(canceled is OperationCanceledException, "caller cancellation affects only its waiter");
            lease.Dispose(); lease = default;
            await fixture.AScope.Entered.Task.WaitAsync(Bound);
            stop = fixture.Client.StopAsync().AsTask();
            Ensure(fixture.Client.State == SharpLinkConnectionState.Draining && !stop.IsCompleted && fixture.BScope.Disposals == 0,
                "Stop must join physical A cleanup before dependent B, despite earlier false/canceled waiters");
            fixture.AScope.Release.Set();
            await stop.WaitAsync(Bound);
            Ensure(fixture.AScope.Disposals == 1 && fixture.BScope.Disposals == 1 && fixture.Runtime.Authority.Bytes == 0,
                "ordered Stop joins each scope once and releases final Runtime metadata");
        }
        catch (Exception exception) { primary = exception; throw; }
        finally { lease.Dispose(); await fixture.CleanupAsync(primary, stop); }
    }

    [Test]
    public async Task RawAdapterCancellationSurvivesCallerAndPhysicalStopWithoutBlockingDependencyRefund()
    {
        var fixture = new Fixture();
        var raw = new OperationCanceledException("actual-adapter-cleanup");
        Exception? primary = null;
        Task<Exception>? unregister = null;
        try
        {
            fixture.AScope.Hold = true; fixture.AScope.Failure = raw;
            unregister = Failure(async () => await fixture.Client.UnregisterAssemblyAsync(fixture.A, TimeSpan.Zero));
            await fixture.AScope.Entered.Task.WaitAsync(Bound);
            Ensure(fixture.BScope.Disposals == 0, "B remains live until throwing A callback physically exits");
            fixture.AScope.Release.Set();
            Ensure(ReferenceEquals(await unregister.WaitAsync(Bound), raw), "public result keeps the exact raw adapter OCE");
            var stopped = await Failure(async () => await fixture.Client.StopAsync());
            Ensure(ReferenceEquals(stopped, raw), "strict physical Stop owns the same sole raw OCE, not canceled-task suppression");
            fixture.AcceptedStop = stopped;
            Ensure(fixture.AScope.Disposals == 1 && fixture.BScope.Disposals == 1 && fixture.Runtime.Authority.Bytes == 0,
                "throwing physical cleanup still independently releases B and final metadata");
        }
        catch (Exception exception) { primary = exception; throw; }
        finally { await fixture.CleanupAsync(primary, unregister); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeclaredDependencyPinProtectsFactoryRollbackAndFinalCommitRejectsDrainingOwner(bool rollbackThrows)
    {
        var fixture = new Fixture(registerA: false);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? primary = null;
        Task<SharpLinkAssemblyRegistrationResult>? registration = null;
        var rollbackFailure = new IOException("candidate-adapter-rollback");
        try
        {
            if (rollbackThrows) fixture.AScope.Failure = rollbackFailure;
            fixture.AScope.DuringDispose = () => ((ITrackedUse)fixture.BScope.Codec!).Use();
            registration = Task.Run(() => fixture.Registry.RegisterPreparedAssembly(fixture.PendingManifest!, commit =>
            {
                ((ITrackedUse)fixture.BScope.Codec!).Use();
                entered.TrySetResult();
                if (!release.Wait(Bound)) throw new TimeoutException("candidate publication");
                return commit();
            }));
            await entered.Task.WaitAsync(Bound);
            Ensure(fixture.BModule.PreparationOwnersForDiagnostics == 1,
                "the real candidate owns one existing cold dependency preparation pin");
            var publicResult = await fixture.Client.UnregisterAssemblyAsync(fixture.B, TimeSpan.Zero);
            var physical = fixture.BModule.RetirementOperation!;
            Ensure(!publicResult.ReferencesReleased && fixture.BScope.Disposals == 0 && !physical.IsCompleted,
                "logical B draining cannot dispose the dependency used by A's prepared callback/rollback");
            release.Set();
            if (rollbackThrows)
            {
                var error = await Failure(async () => await registration!);
                Ensure(error is AggregateException { InnerExceptions.Count: 2 } aggregate &&
                    aggregate.InnerExceptions[0] is InvalidOperationException rejected &&
                    rejected.Message.StartsWith("InvalidObjectState:", StringComparison.Ordinal) &&
                    ReferenceEquals(aggregate.InnerExceptions[1], rollbackFailure),
                    "final dependency rejection and exact rollback failure must both survive");
                fixture.AcceptedWorkerFailure = error;
            }
            else
            {
                var result = await registration.WaitAsync(Bound);
                Ensure(!result.Succeeded && result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "final exact dependency state must reject without replaying the candidate factory");
            }
            Ensure(fixture.AScope.Disposals == 1 && fixture.BScope.Uses == 2,
                "candidate adapter rollback runs once while exact B codec is still usable");
            Ensure((await physical.WaitAsync(Bound)).ReferencesReleased && fixture.BScope.Disposals == 1 &&
                fixture.BModule.PreparationOwnersForDiagnostics == 0,
                "rejected candidate releases its cold pin only after rollback, then B physically retires");
        }
        catch (Exception exception) { primary = exception; throw; }
        finally { release.Set(); await fixture.CleanupAsync(primary, registration); }
    }

    [Test]
    public async Task ActualAlcDependencyCaptureRunsOutsideRegistryGateAndRejectsManagementReentry()
    {
        var fixture = new Fixture();
        Exception? primary = null;
        try
        {
            var missing = $"MissingDependency.{Guid.NewGuid():N}";
            var owner = CreateOwner(fixture.Context, 3, missing);
            var gate = (Lock)typeof(ClientAssemblyRegistry).GetField("_gate",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Registry)!;
            var callbacks = 0;
            var gateHeld = false;
            Exception? reentry = null;
            fixture.Context.OnMissing = requested =>
            {
                if (requested.Name != missing) return;
                callbacks++;
                gateHeld |= gate.IsHeldByCurrentThread;
                reentry = Capture(() => fixture.Client.UnregisterAssemblyAsync(fixture.B, TimeSpan.Zero));
            };
            var result = fixture.Client.RegisterAssembly(owner);
            Ensure(!result.Succeeded && result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency && callbacks == 1,
                "unresolved exact declaration fails once before factories; later validation never rebinds");
            Ensure(!gateHeld, "actual ALC resolver must execute outside the real registry gate");
            RequireReentry(reentry);
            Ensure(!fixture.Registry.DynamicModules.ContainsKey(owner) && fixture.BScope.Disposals == 0,
                "failed capture/admission cannot publish a candidate or retire its existing dependency");
        }
        catch (Exception exception) { primary = exception; throw; }
        finally { await fixture.CleanupAsync(primary); }
    }

    private sealed class Fixture
    {
        // Failure-only quarantine: a timeout is not permission to unload an executing owner.
        private static readonly Lock IncompleteGate = new();
        private static readonly List<Fixture> IncompleteFixtures = [];
        private Task? _cleanupStop;
        private Task? _cleanupTransport;
        private readonly Dictionary<Task, Task<Exception?>> _cleanupObservers = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Task> _observedCleanupOutcomes = new(ReferenceEqualityComparer.Instance);
        private readonly OwnerContext _context = new();
        internal OwnerContext Context => _context;
        private readonly TestClientTransportFactory _transport = new();
        internal readonly SharpLinkClient Client;
        internal readonly SharpLinkRuntimeContext Runtime;
        internal readonly Assembly A;
        internal readonly Assembly B;
        internal readonly Scope AScope;
        internal readonly Scope BScope;
        internal readonly SharpLinkDynamicModule AModule;
        internal readonly SharpLinkDynamicModule BModule;
        internal readonly ClientAssemblyRegistry Registry;
        internal PreparedGeneratedManifest? PendingManifest;
        private ColdMetadataPreparation? _pendingPreparation;
        internal readonly ulong BaselineBytes;
        internal Exception? AcceptedStop;
        internal Exception? AcceptedWorkerFailure;
        internal Fixture(bool registerA = true)
        {
            try
            {
                Client = (SharpLinkClient)SharpClientBuilder.Create().DisableRequestTimeout()
                    .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(_transport).Build();
                Runtime = (SharpLinkRuntimeContext)Client.RuntimeContext;
                BaselineBytes = Runtime.Authority.Bytes;
                B = CreateOwner(_context, 2, string.Empty);
                A = CreateOwner(_context, 1, B.FullName!);
                var b = Client.RegisterAssembly(B); Ensure(b.Succeeded, $"real B registration failed: {b.Error}");
                Registry = (ClientAssemblyRegistry)(typeof(SharpLinkClient).GetField("_assemblyRegistry",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client)!);
                BModule = Registry.DynamicModules[B];
                BScope = ((ManifestBase)((PreparedGeneratedManifest)BModule.Manifest).OriginalManifest).Adapter;
                if (registerA)
                {
                    var a = Client.RegisterAssembly(A); Ensure(a.Succeeded, $"real A registration failed: {a.Error}");
                    // Read-only initial observation of real installed modules, before concurrent cleanup.
                    AModule = Registry.DynamicModules[A];
                    AScope = ((ManifestBase)((PreparedGeneratedManifest)AModule.Manifest).OriginalManifest).Adapter;
                    Ensure(SharpLinkGeneratedDependencyBinding.ManifestDependsOn(AModule.Manifest, B),
                        "declared A dependency resolves to the exact registered B Assembly object");
                }
                else
                {
                    AModule = null!; // This branch deliberately has no published A module.
                    _pendingPreparation = Runtime.Domain.BeginPreparation();
                    var loaded = SharpLinkAssemblyManifestLoader.TryLoad(A, _pendingPreparation, out var manifest);
                    Ensure(loaded.Succeeded && manifest is not null, $"real A locator failed: {loaded.Error}");
                    PendingManifest = manifest!;
                    AScope = ((ManifestBase)PendingManifest.OriginalManifest).Adapter;
                }
            }
            catch (Exception original)
            {
                // Use the same independent cleanup even when construction stopped mid-stage.
                // Cleanup aggregates any secondary failures with this exact original instance.
                CleanupAsync(original).GetAwaiter().GetResult();
                throw;
            }
        }
        private static async Task<Exception?> ObserveOutcome(Task task)
        {
            try { await task.ConfigureAwait(false); return null; }
            catch (Exception error) { return error; }
        }

        internal async Task CleanupAsync(Exception? primary, params Task?[] workers)
        {
            List<Exception>? failures = null;
            void Add(Exception error) => (failures ??= []).Add(error);
            async Task Join(Task task, Exception? accepted = null)
            {
                if (_observedCleanupOutcomes.Contains(task)) return;
                if (!_cleanupObservers.TryGetValue(task, out var observer))
                {
                    observer = ObserveOutcome(task);
                    _cleanupObservers.Add(task, observer);
                }
                Exception? outcome;
                try { outcome = await observer.WaitAsync(Bound); }
                catch (Exception waitFailure) { Add(waitFailure); return; }
                _observedCleanupOutcomes.Add(task);
                if (outcome is not null && !ReferenceEquals(outcome, accepted)) Add(outcome);
            }
            void ReleasePendingIfSafe()
            {
                if (workers.Any(worker => worker is not null && !worker.IsCompleted)) return;
                try { _pendingPreparation?.Dispose(); _pendingPreparation = null; PendingManifest = null; }
                catch (Exception error) { Add(error); }
            }
            try { AScope?.Release.Set(); } catch (Exception error) { Add(error); }
            try { BScope?.Release.Set(); } catch (Exception error) { Add(error); }
            foreach (var worker in workers)
                if (worker is not null) await Join(worker, AcceptedWorkerFailure);
            ReleasePendingIfSafe();
            try
            {
                if (Client is not null) _cleanupStop ??= Client.StopAsync().AsTask();
            }
            catch (Exception error) { Add(error); }
            if (_cleanupStop is not null) await Join(_cleanupStop, AcceptedStop);
            // Transport cleanup is independent of a timed-out/faulted Stop, then original owners
            // are rejoined rather than replacing them with completed placeholder tasks.
            try { _cleanupTransport ??= _transport.DisposeAsync().AsTask(); }
            catch (Exception error) { Add(error); }
            if (_cleanupTransport is not null) await Join(_cleanupTransport);
            foreach (var worker in workers)
                if (worker is not null) await Join(worker, AcceptedWorkerFailure);
            ReleasePendingIfSafe();
            if (_cleanupStop is not null) await Join(_cleanupStop, AcceptedStop);
            if (_cleanupTransport is not null) await Join(_cleanupTransport);

            var ownersExited = workers.All(worker => worker?.IsCompleted ?? true) &&
                (Client is null || _cleanupStop is { IsCompleted: true }) &&
                _cleanupTransport is { IsCompleted: true } && _pendingPreparation is null &&
                _cleanupObservers.All(pair => pair.Value.IsCompleted && _observedCleanupOutcomes.Contains(pair.Key));
            try
            {
                if (Client is not null)
                {
                    var registry = (ClientAssemblyRegistry)typeof(SharpLinkClient).GetField("_assemblyRegistry",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client)!;
                    var gate = (Lock)typeof(ClientAssemblyRegistry).GetField("_gate",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
                    var operations = (Dictionary<Assembly, (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module)>)
                        typeof(ClientAssemblyRegistry).GetField("_unregisterOperations",
                            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
                    lock (gate)
                        ownersExited &= registry.DynamicModules.Values.All(module => module.State == SharpLinkDynamicModuleState.Released) &&
                            operations.Values.All(operation => operation.Task.IsCompleted &&
                                (operation.Module.RetirementObservation?.IsCompleted ?? false));
                    ownersExited &= Client.FrameworkTaskSnapshotForDiagnostics.ActiveTasks == 0;
                }
            }
            catch (Exception error) { ownersExited = false; Add(error); }
            if (ownersExited)
            {
                try { AScope?.Release.Dispose(); } catch (Exception error) { Add(error); }
                try { BScope?.Release.Dispose(); } catch (Exception error) { Add(error); }
                try { _context.Unload(); } catch (Exception error) { Add(error); }
            }
            else
            {
                lock (IncompleteGate) IncompleteFixtures.Add(this);
                Add(new InvalidOperationException("Fixture physical cleanup is incomplete; exact tasks, roots and barriers retained."));
            }
            if (failures is not null)
            {
                if (primary is not null) failures.Insert(0, primary);
                throw new AggregateException(failures);
            }
        }
    }

    private static Assembly CreateOwner(OwnerContext context, long identity, string dependency)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName($"SharpLink.DependencyCleanup.{Guid.NewGuid():N}"), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("CurrentOwner");
        module.DefineType("OwnedValue", TypeAttributes.Public | TypeAttributes.Sealed).CreateType();
        var manifest = module.DefineType("OwnedManifest", TypeAttributes.Public | TypeAttributes.Sealed, typeof(ManifestBase));
        var constructor = manifest.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var il = constructor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I8, identity); il.Emit(OpCodes.Ldstr, dependency);
        il.Emit(OpCodes.Call, typeof(ManifestBase).GetConstructor([typeof(long), typeof(string)])!); il.Emit(OpCodes.Ret);
        var type = manifest.CreateType();
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(SharpLinkGeneratedAssemblyManifestAttribute).GetConstructor(
            [typeof(Type), typeof(int), typeof(int), typeof(string), typeof(string)])!,
            [type, SharpLinkGeneratedManifestVersions.Api, SharpLinkGeneratedManifestVersions.Protocol,
             "dependency-cleanup", SharpLinkGeneratedManifestVersions.AbiIdentity]));
        using var image = new MemoryStream(); assembly.Save(image); image.Position = 0; return context.LoadFromStream(image);
    }
    private sealed class OwnerContext() : AssemblyLoadContext($"DependencyCleanup.{Guid.NewGuid():N}", isCollectible: true)
    {
        internal Action<AssemblyName>? OnMissing;
        protected override Assembly? Load(AssemblyName name)
        {
            var existing = Assemblies.Concat(Default.Assemblies).FirstOrDefault(candidate => AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), name));
            if (existing is not null) return existing;
            OnMissing?.Invoke(name);
            return null;
        }
    }
    public class ManifestBase : ISharpLinkGeneratedAssemblyManifest
    {
        public ManifestBase(long identity, string dependency)
        {
            var target = GetType().Assembly.GetType("OwnedValue", throwOnError: true)!;
            RpcAssemblyHash = new((ulong)identity, 0x775DE);
            Dependencies = dependency.Length == 0 ? [] : [dependency];
            Adapter = new Scope(); Codecs = [new Factory(target, Adapter)];
        }
        public Scope Adapter { get; }
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "dependency-cleanup";
        public Assembly OwnerAssembly => GetType().Assembly;
        public RpcHash128 RpcAssemblyHash { get; }
        public string CompileTimeDescriptor => "dependency-cleanup";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } = [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; } = [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; }
        public IReadOnlyList<string> Dependencies { get; }
    }
    private sealed class Factory(Type type, Scope scope) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => type;
        public RpcHash128 CodecHash => new(0x775DE, 1);
        public string AdapterId => scope.AdapterId;
        public IRpcCodecAdapter Adapter => scope;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapter)
        {
            Ensure(ReferenceEquals(adapter, scope), "exact prepared adapter scope");
            return scope.Codec = (IRpcCodec)Activator.CreateInstance(typeof(TrackedCodec<>).MakeGenericType(type), scope)!;
        }
        public bool IsCompatibleCodec(IRpcCodec codec) => typeof(IRpcCodec<>).MakeGenericType(type).IsInstanceOfType(codec);
    }
    public sealed class Scope : IRpcCodecAdapter, IRpcCodecAdapterScope
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal bool Hold;
        internal int Disposals;
        internal int Uses;
        internal int Disposed;
        internal Action? DuringDispose;
        internal Exception? Failure;
        internal IRpcCodec? Codec;
        public string AdapterId => "test.dependency-cleanup/v1";
        public IRpcCodecAdapterScope CreateScope() => this;
        public IRpcCodec<T> CreateCodec<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() => new TrackedCodec<T>(this);
        public void Dispose()
        {
            Interlocked.Increment(ref Disposals); Entered.TrySetResult();
            try
            {
                if (Hold && !Release.Wait(Bound)) throw new TimeoutException("adapter release");
                DuringDispose?.Invoke();
                if (Failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(Failure).Throw();
            }
            finally { Volatile.Write(ref Disposed, 1); }
        }
    }
    public interface ITrackedUse { void Use(); }
    public sealed class TrackedCodec<T>(Scope scope) : IRpcCodec<T>, ITrackedUse
    {
        public void Serialize(in T value, IBufferWriter<byte> buffer) => Touch();
        public T? Deserialize(in ReadOnlySequence<byte> buffer) { Touch(); return default; }
        public void Use() => Deserialize(ReadOnlySequence<byte>.Empty);
        private void Touch()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref scope.Disposed) != 0, this);
            Interlocked.Increment(ref scope.Uses);
        }
    }
    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception exception) { return exception; } }
    private static async Task<Exception> Failure(Func<Task> action)
    {
        try { await action().WaitAsync(Bound); }
        catch (Exception exception) { return exception; }
        throw new InvalidOperationException("expected an actual failure");
    }
    private static void RequireReentry(Exception? error)
        => Ensure(error is ColdMetadataException { Code: SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState },
            "same-domain cleanup callback cannot await management or Stop");
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
