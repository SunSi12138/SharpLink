using System.Collections.Frozen;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using SharpLink.Client;
using SharpLink.Server;
using SharpLink.RetirementEntryPlugin;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel("retirement-entry-plugin")]
public sealed class RetirementEntryContractTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly SemaphoreSlim Isolation = new(1, 1);
    private static readonly List<Fixture> Containment = [];

    [Test]
    public Task ServerPreCanceledReplacementPublishesAndOnlyCancelsCallerWait()
        => Run(Mode.Server, async f =>
        {
            f.AcquireCall();
            var replacement = f.NewAssembly(blockLoader: false);
            using var caller = new CancellationTokenSource(); caller.Cancel();
            var waiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly, TimeSpan.FromSeconds(5), caller.Token)));
            await f.Within(f.Clock.Armed.Task); f.AssertPublished(replacement.Assembly);
            var failure = await f.Failure(waiter);
            Ensure(failure is OperationCanceledException && !f.Module.RetirementOperation!.IsCompleted,
                "pre-canceled caller leaves, not the original physical owner");
            f.Clock.Advance(TimeSpan.FromSeconds(5)); await f.Within(f.Cancelled.Task);
            await f.FinishRetirement(); f.ExpectPrivateCancellation(failure!, caller.Token);
        });

    [Test]
    public Task ServerCancellationDuringSynchronousPreparationCannotVetoReplacement()
        => Run(Mode.Server, async f =>
        {
            f.AcquireCall(); f.BlockCancellation = true;
            var replacement = f.NewAssembly(); using var caller = new CancellationTokenSource();
            var invocation = f.Invoke(() => f.Replace(replacement.Assembly, TimeSpan.FromSeconds(5), caller.Token));
            await f.Within(replacement.Entered.Task); caller.Cancel(); f.Clock.Advance(TimeSpan.FromSeconds(5));
            Ensure(!invocation.IsCompleted && f.Module.State == SharpLinkDynamicModuleState.Running && f.Cancels == 0,
                "synchronous preparation remains synchronous; no old-module cancellation before publication");
            replacement.Release.TrySetResult(); var waiter = await f.Result(invocation);
            await f.Within(f.Cancelled.Task); f.AssertPublished(replacement.Assembly);
            var failure = await f.Failure(waiter); f.CallbackRelease.Set();
            await f.FinishRetirement(); f.ExpectPrivateCancellation(failure!, caller.Token);
        });

    [Test]
    public Task ServerSourceCancellationWithLiveCallerRemainsStructuredRejection()
        => PreparationCancellation(canceledCaller: false);

    [Test]
    public Task ServerSourceCancellationWithCanceledCallerRemainsStructuredRejection()
        => PreparationCancellation(canceledCaller: true);

    private static Task PreparationCancellation(bool canceledCaller)
        => Run(Mode.Server, async f =>
        {
            var marker = new OperationCanceledException("entry candidate source marker");
            var replacement = f.NewAssembly(stubFailure: marker); using var caller = new CancellationTokenSource();
            var invocation = f.Invoke(() => f.Replace(replacement.Assembly, TimeSpan.Zero, caller.Token));
            await f.Within(replacement.Entered.Task); if (canceledCaller) caller.Cancel();
            replacement.Release.TrySetResult(); var result = await f.Result(await f.Result(invocation));
            Ensure(!result.Succeeded && result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                result.Error.Artifact == "DeclaredMethodFacts" &&
                result.Error.Message.Contains("OperationCanceledException: entry candidate source marker", StringComparison.Ordinal),
                "source cancellation remains structured, independent of caller state");
            Ensure(replacement.Count(nameof(EntryState.StubCount)) == 1 && replacement.Count(nameof(EntryState.ScopeCreated)) == 1 &&
                replacement.Count(nameof(EntryState.ScopeDisposeEntered)) == 1 && replacement.Count(nameof(EntryState.ScopeDisposeExited)) == 1,
                "actual candidate stub and eager codec scope entered and exited once");
            f.AssertOldRunning();
        });

    [Test]
    public Task ServerSourceAndRollbackFailuresKeepIndependentOccurrences()
        => Run(Mode.Server, async f =>
        {
            var marker = new OperationCanceledException("entry source and cleanup marker");
            var replacement = f.NewAssembly(stubFailure: marker, cleanupFailure: marker);
            var invocation = f.Invoke(() => f.Replace(replacement.Assembly, TimeSpan.Zero));
            await f.Within(replacement.Entered.Task); replacement.Release.TrySetResult();
            var failure = await f.Failure(invocation);
            Ensure(failure is AggregateException && Count(failure, marker) == 2 && Only(failure, marker),
                "source and actual rollback are two occurrences of the same predefined raw marker");
            Ensure(replacement.Count(nameof(EntryState.StubCount)) == 1 &&
                replacement.Count(nameof(EntryState.ScopeDisposeEntered)) == 1 && replacement.Count(nameof(EntryState.ScopeDisposeExited)) == 1,
                "known synchronous cleanup scope actually exited after throwing");
            f.Expected.Add(marker); f.AssertOldRunning();
        });

    [Test]
    public Task ServerPostcommitCallerCancellationLeavesReplacementOwnerIntact()
        => Run(Mode.Server, async f =>
        {
            f.AcquireCall(); f.BlockCancellation = true; var replacement = f.NewAssembly(blockLoader: false);
            using var caller = new CancellationTokenSource();
            var waiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly, TimeSpan.Zero, caller.Token)));
            await f.Within(f.Cancelled.Task); f.AssertPublished(replacement.Assembly); caller.Cancel();
            var failure = await f.Failure(waiter); f.ReleaseCall();
            Ensure(!f.Module.RetirementOperation!.IsCompleted, "actual callback exit still owns physical cleanup");
            f.CallbackRelease.Set(); await f.FinishRetirement(); f.ExpectPrivateCancellation(failure!, caller.Token);
        });

    [Test]
    public Task RealCompletedRetirementWinsCanceledCompletionWait()
        => Run(Mode.Server, async f =>
        {
            var replacement = f.NewAssembly(blockLoader: false);
            Ensure((await f.Result(await f.Result(f.Invoke(() => f.Replace(replacement.Assembly, TimeSpan.Zero))))).Succeeded,
                "real replacement completed");
            await f.FinishRetirement(); var original = f.Module.RetirementOperation!;
            using var caller = new CancellationTokenSource(); caller.Cancel();
            await RetirementWaiter.WaitForCompletionAsync(original, caller.Token);
            Ensure((await original).ReferencesReleased && original.IsCompletedSuccessfully,
                "exact terminal source wins at the helper boundary; not a second public replacement");
        });

    [Test]
    public Task MultiClusterNonFirstChildReplacementConsumesOwnOuterBudget()
        => Run(Mode.Multi, async f =>
        {
            f.AcquireCall(); var replacement = f.NewAssembly(); var firstTimestamp = f.FirstClock!.Peek; var entry = f.Clock.Peek;
            var invocation = f.Invoke(() => f.Replace(replacement.Assembly, TimeSpan.FromSeconds(5)));
            await f.Within(replacement.Entered.Task); f.Clock.Advance(TimeSpan.FromSeconds(5)); f.AssertOldRunning();
            replacement.Release.TrySetResult(); var waiter = await f.Result(invocation);
            await f.Within(f.Cancelled.Task); f.AssertPublished(replacement.Assembly); var budget = f.Budget;
            Ensure(ReferenceEquals(budget.Provider, f.Clock) && budget.TimestampFrequency == 3 &&
                budget.TimestampOrigin == entry && budget.Duration == TimeSpan.FromSeconds(5) && f.FirstClock.Peek == firstTimestamp,
                "exact selected provider/frequency/origin survives outer preparation");
            Ensure((await f.Result(waiter)).Succeeded, "outer publication succeeded"); await f.FinishRetirement();
        });

    [Test]
    public Task MultiClusterFailedOuterPreparationDoesNotCancelOldModule()
        => Run(Mode.Multi, async f =>
        {
            f.AcquireCall(); var replacement = f.NewAssembly();
            var invocation = f.Invoke(() => f.Replace(replacement.Assembly, TimeSpan.FromSeconds(5)));
            await f.Within(replacement.Entered.Task); f.Clock.Advance(TimeSpan.FromSeconds(5));
            var marker = new InvalidOperationException("outer loader rejection marker");
            Ensure(replacement.Release.TrySetException(marker), "exact loader marker injected once");
            var result = await f.Result(await f.Result(invocation));
            var expected = new SharpLinkAssemblyRegistrationError(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                "The generated SharpLink manifest could not be loaded: TargetInvocationException: " + new TargetInvocationException(marker).Message,
                SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(replacement.Assembly),
                IncomingLoadContext: SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(replacement.Assembly));
            Ensure(ReferenceEquals(replacement.ManifestFailureObserved, marker) && replacement.Count(nameof(EntryState.ManifestCount)) == 1 &&
                replacement.Count(nameof(EntryState.ScopeCreated)) == 0 && replacement.Count(nameof(EntryState.StubCount)) == 0 &&
                !result.Succeeded && result.Error == expected, "actual loader marker, exact structured activation error, no scope or stub");
            f.AssertOldRunning(); f.AssertRoute(f.OldAssembly);
        });

    [Test]
    public Task MultiClusterQueuedUnregisterDoesNotRestartCapturedBudget()
        => Run(Mode.Multi, async f =>
        {
            f.AcquireCall(); var entry = f.Clock.Peek; var queue = f.NewQueue();
            var waiter = await f.Result(f.InvokeQueued(queue, () => f.Unregister(TimeSpan.FromSeconds(5))));
            await f.Within(queue.Posted.Task); Ensure(f.Module.RetirementOperation is null, "no child election before queued continuation");
            f.Clock.Advance(TimeSpan.FromSeconds(5)); await f.Dispatch(queue); await f.Within(f.Cancelled.Task);
            Ensure(f.Budget.TimestampOrigin == entry && f.Budget.Duration == TimeSpan.FromSeconds(5), "no post-yield budget reset");
            Ensure(!(await f.Result(waiter)).ReferencesReleased, "held actual call yields deferred result"); await f.FinishRetirement();
        });

    [Test]
    public Task MultiClusterRepeatedUnregisterKeepsOuterWinnerBudget()
        => Run(Mode.Multi, async f =>
        {
            f.AcquireCall(); f.Clock.BlockNextSample(); var first = f.Invoke(() => f.Unregister(TimeSpan.FromDays(1)));
            await f.Within(f.Clock.SampleEntered.Task);
            var second = await f.Result(f.Invoke(() => f.Unregister(TimeSpan.FromSeconds(3)))); await f.Within(f.Clock.Armed.Task);
            var original = f.Module.RetirementOperation; var budget = f.Budget;
            f.Clock.SampleRelease.Set(); var loser = await f.Result(first);
            Ensure(ReferenceEquals(original, f.Module.RetirementOperation) && f.Budget.Duration == TimeSpan.FromSeconds(3),
                "election winner, not earliest arrival, owns original budget");
            f.Clock.Advance(TimeSpan.FromSeconds(3)); await f.Within(f.Cancelled.Task);
            Ensure(!(await f.Result(second)).ReferencesReleased && !(await f.Result(loser)).ReferencesReleased &&
                f.Budget.TimestampOrigin == budget.TimestampOrigin && f.Cancels == 1, "duplicate neither resets nor recancels");
            await f.FinishRetirement();
        });

    [Test]
    public Task MultiClusterDirectChildElectionKeepsItsOriginalBudget()
        => Run(Mode.Multi, async f =>
        {
            f.AcquireCall(); var queue = f.NewQueue();
            var outer = await f.Result(f.InvokeQueued(queue, () => f.Unregister(TimeSpan.FromDays(1)))); await f.Within(queue.Posted.Task);
            var direct = f.Own(f.Child!.UnregisterAssemblyAsync(f.OldAssembly, TimeSpan.FromSeconds(3)).AsTask());
            await f.Within(f.Clock.Armed.Task); var original = f.Module.RetirementOperation; await f.Dispatch(queue);
            f.Clock.Advance(TimeSpan.FromSeconds(3)); await f.Within(f.Cancelled.Task);
            Ensure(!(await f.Result(direct)).ReferencesReleased && !(await f.Result(outer)).ReferencesReleased &&
                ReferenceEquals(original, f.Module.RetirementOperation) && f.Budget.Duration == TimeSpan.FromSeconds(3) && f.Cancels == 1,
                "direct child election remains authoritative"); await f.FinishRetirement();
        });

    [Test]
    public Task MultiClusterCaptureFailurePreservesRoutesAndRunningModule()
        => Run(Mode.Multi, async f =>
        {
            var marker = new InvalidOperationException("capture marker"); f.Clock.FailNextSample(marker);
            var invocation = f.Invoke(() => f.Unregister(TimeSpan.Zero));
            Ensure(ReferenceEquals(await f.Failure(invocation), marker), "original pre-election provider failure preserved");
            f.Expected.Add(marker); f.AssertOldRunning(); f.AssertRoute(f.OldAssembly);
            Ensure(f.Clock.FailedSamples == 1, "no fallback or recapture");
        });

    [Test]
    public Task MultiClusterReplacementRejectsDisposedRegistrationAfterLoader()
        => Run(Mode.Multi, async f =>
        {
            var registration = f.Registration; var replacement = f.NewAssembly();
            var invocation = f.Invoke(() => f.Replace(replacement.Assembly, TimeSpan.FromSeconds(5)));
            await f.Within(replacement.Entered.Task);
            Ensure((await f.Result(f.Unregister(TimeSpan.Zero))).ReferencesReleased, "competing real unregister released old module");
            await f.FinishRetirement();
            Ensure(Read<object?>(registration, "_manifest") is null && !f.HasRegistration(registration),
                "exact old registration actually disposed before releasing loader");
            replacement.Release.TrySetResult(); var result = await f.Result(await f.Result(invocation));
            Ensure(!result.Succeeded && result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                "third gate rejects structurally, not ObjectDisposedException");
            Ensure(!f.IsChildRegistered(replacement.Assembly) && !f.HasRoute(replacement.Assembly), "no stale new child or route publication");
        });

    [Test]
    public Task SharedProviderCannotTransferBudgetBetweenChildRegistries()
        => Run(Mode.SharedProvider, async f =>
        {
            var a = (ISharpLinkPreparedAssemblyClient)f.Child!; var b = (ISharpLinkPreparedAssemblyClient)f.FirstChild!;
            var budget = a.CaptureRetirementBudget(TimeSpan.FromSeconds(5));
            var cross = f.Invoke(() => b.UnregisterAssemblyAsync(f.OldAssembly, budget).AsTask()); var failure = await f.Failure(cross);
            Ensure(failure is InvalidOperationException && failure.Message == "The retirement budget belongs to another exact child registry.",
                "shared provider cannot cross the exact registry boundary"); f.Expected.Add(failure!);
            var unbound = f.Invoke(() => a.UnregisterAssemblyAsync(f.OldAssembly, default(ClientRetirementBudget)).AsTask());
            var unboundFailure = await f.Failure(unbound);
            Ensure(unboundFailure is InvalidOperationException && unboundFailure.Message == "The retirement budget belongs to another exact child registry.",
                "default capability fails at exact binding boundary"); f.Expected.Add(unboundFailure!);
            f.AssertOldRunning(); f.AcquireCall(); var waiter = f.Own(a.UnregisterAssemblyAsync(f.OldAssembly, budget).AsTask());
            await f.Within(f.Clock.Armed.Task);
            Ensure(ReferenceEquals(f.Budget.Provider, f.Clock) && f.Budget.TimestampOrigin == budget.GetFor(f.Registry).TimestampOrigin,
                "correct owner preserves original capability");
            f.ReleaseCall(); Ensure((await f.Result(waiter)).ReferencesReleased, "correct child actually released"); await f.FinishRetirement();
        });

    [Test]
    public Task UnsupportedInternalChildIsRejectedBeforeRetirementPublication()
        => Run(Mode.Unsupported, async f =>
        {
            var replacement = f.NewAssembly(); var invocation = f.Invoke(() => f.Unregister(TimeSpan.Zero));
            var failure = await f.Failure(invocation);
            Ensure(failure is InvalidOperationException && failure.Message == "The internal child does not support exact retirement budgets.",
                "unsupported unregister rejects explicitly before publication"); f.Expected.Add(failure!);
            var replacementInvocation = f.Invoke(() => f.Replace(replacement.Assembly, TimeSpan.Zero));
            var result = await f.Result(await f.Result(replacementInvocation));
            Ensure(!result.Succeeded && result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState &&
                replacement.Count(nameof(EntryState.ManifestCount)) == 0 && !replacement.Entered.Task.IsCompleted,
                "unsupported replacement rejects before loading"); f.AssertRoute(f.OldAssembly);
        });

    private enum Mode { Server, Multi, SharedProvider, Unsupported }
    private static async Task Run(Mode mode, Func<Fixture, Task> body)
    {
        Ensure(await Isolation.WaitAsync(Bound), "actual bounded semaphore admission");
        Fixture? fixture = null;
        try
        {
            // Construction is inert bookkeeping. All host/plugin work is rooted
            // by the original initialization invocation before it is observed.
            fixture = new Fixture(mode);
            Exception? failure = null;
            try { await fixture.Within(fixture.InitializeOwned()); await body(fixture); }
            catch (Exception exception) { failure = exception; }
            await fixture.Close(failure);
        }
        finally
        {
            if (fixture is not null && !fixture.OwnersEnded)
            { lock (Containment) Containment.Add(fixture); }
            else Isolation.Release();
        }
    }

    private sealed class Fixture(Mode mode)
    {
        internal readonly StageClock Clock = new(mode == Mode.Server ? TimeSpan.TicksPerSecond : 3,
            mode == Mode.Server ? 0 : -1_000_003);
        internal StageClock? FirstClock;
        internal SharpLinkClient? Child;
        internal SharpLinkClient? FirstChild;
        private SharpLinkServer? _server;
        private SharpLinkMultiClusterClient? _multi;
        private bool _childrenCaptured;
        private SharpLinkDynamicModule? _module;
        internal SharpLinkDynamicModule Module => _module ?? throw new InvalidOperationException("No actual module selected.");
        internal readonly Assembly OldAssembly = typeof(EntryManifest).Assembly;
        internal ClientAssemblyRegistry Registry => Read<ClientAssemblyRegistry>(Child!, "_assemblyRegistry");
        private readonly SharpLinkClusterKey _firstKey = new("entry-a");
        private readonly SharpLinkClusterKey _key = new("entry-b");
        private readonly Lock _tasksGate = new();
        private readonly Lock _leaseGate = new();
        private readonly List<Task> _tasks = [];
        private readonly List<Task> _invocations = [];
        private readonly Dictionary<Task, Task<Exception?>> _outcomes = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _stopping = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, Task> _hostStops = new(ReferenceEqualityComparer.Instance);
        private Task? _initialization;
        private readonly List<SharpLinkDynamicModule> _modules = [];
        private readonly List<PluginAssembly> _plugins = [];
        private readonly List<OwnedQueue> _queues = [];
        internal readonly List<Exception> Expected = [];
        internal readonly TaskCompletionSource Cancelled = Signal();
        internal readonly ManualResetEventSlim CallbackRelease = new(false);
        internal bool BlockCancellation;
        internal int Cancels;
        internal bool OwnersEnded;
        private SharpLinkDynamicModuleLease _call;
        private CancellationTokenRegistration _cancellation;
        private ColdMetadataPreparation? _injectedPreparation;
        private SharpLinkClusterSlot? _injectedSlot;
        private IDisposable? _untransferredCoordinatorLease;
        private Task[] Tasks { get { lock (_tasksGate) return _tasks.ToArray(); } }
        private Task[] Invocations { get { lock (_tasksGate) return _invocations.ToArray(); } }
        private Task[] Outcomes { get { lock (_tasksGate) return _outcomes.Values.Cast<Task>().ToArray(); } }

        internal Task<T> Own<T>(Task<T> task) { lock (_tasksGate) if (!_tasks.Contains(task)) _tasks.Add(task); return task; }
        internal Task Own(Task task) { lock (_tasksGate) if (!_tasks.Contains(task)) _tasks.Add(task); return task; }
        internal Task<T> Invoke<T>(Func<T> action)
        {
            var task = Task.Factory.StartNew(() => { Clock.ClaimSampleHook(); return action(); }, CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            lock (_tasksGate) { _tasks.Add(task); _invocations.Add(task); }
            return task;
        }
        internal Task<T> InvokeQueued<T>(OwnedQueue queue, Func<T> action) => Invoke(() =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(queue);
            try { return action(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        internal OwnedQueue NewQueue() { var queue = new OwnedQueue(); _queues.Add(queue); return queue; }
        internal async Task Dispatch(OwnedQueue queue)
        {
            while (queue.TryTake(out var item))
                await Result(Invoke(() =>
                { try { item.Callback(item.State); return true; } finally { queue.CompleteDispatch(); } }));
        }
        internal Task InitializeOwned() => _initialization = Own(JoinInvocation(Invoke(() => Own(Initialize()))));

        private async Task Initialize()
        {
            Ensure(EntryState.ManifestEntered is null && EntryState.ManifestRelease is null &&
                EntryState.StubFailure is null && EntryState.ScopeFailure is null && EntryState.ManifestFailureObserved is null,
                "default plugin state has no injected callback");
            if (mode == Mode.Server)
            {
                _server = (SharpLinkServer)SharpLinkServerBuilder.Create()
                    .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                    .UseTimeProvider(Clock).UseTransport(new Listener()).Build();
                await Within(Own(_server.StartAsync().AsTask()));
                Ensure(_server.LifecycleState == SharpLinkServerLifecycleState.Running, "actual Server Start");
                Ensure(_server.RegisterAssembly(OldAssembly).Succeeded, "actual Server dynamic publication");
                _module = Read<ServerServiceModuleRegistry>(_server, "_serviceModuleRegistry").DynamicModules[OldAssembly];
            }
            else if (mode == Mode.Unsupported)
            {
                InitializeUnsupported(); return;
            }
            else
            {
                FirstClock = mode == Mode.SharedProvider ? Clock : new StageClock(TimeSpan.TicksPerSecond, 123);
                _multi = (SharpLinkMultiClusterClient)SharpLinkMultiClusterClientBuilder.Create()
                    .UseGeneratedDiscoverySources(FixedGeneratedManifestSource.Empty, FixedGeneratedClusterRouteSource.Empty)
                    .DisableRequestTimeout()
                    .AddCluster(_firstKey, builder => builder.UseTimeProvider(FirstClock!).UseTransport(new Connector()),
                        slot => slot.AllowDynamicContracts = true)
                    .AddCluster(_key, builder => builder.UseTimeProvider(Clock).UseTransport(new Connector()),
                        slot => slot.AllowDynamicContracts = true).Build();
                // Build already owns both real children, even if Start later
                // faults or times out. Root them before any asynchronous gap.
                WithSnapshot(snapshot =>
                {
                    Ensure(snapshot.Clusters.Count == 2, "capture the complete actual child set");
                    FirstChild = (SharpLinkClient)snapshot.Clusters[_firstKey].Client;
                    Child = (SharpLinkClient)snapshot.Clusters[_key].Client; return true;
                });
                _childrenCaptured = true;
                await Within(Own(_multi.StartAsync().AsTask()));
                Ensure(_multi.LifecycleState == SharpLinkClientLifecycleState.Running &&
                    Child!.LifecycleState == SharpLinkClientLifecycleState.Running &&
                    FirstChild!.LifecycleState == SharpLinkClientLifecycleState.Running, "actual coordinator and both children started");
                Ensure(ReferenceEquals(Read<TimeProvider>(_multi, "_timeProvider"), FirstClock),
                    "selected child differs from coordinator first-provider source");
                Ensure(_multi.RegisterAssembly(_key, OldAssembly).Succeeded, "actual routed child publication");
                _module = Registry.DynamicModules[OldAssembly]; AssertRoute(OldAssembly);
            }
            Remember(Module);
            _cancellation = Module.ForcedCancellation.Register(() =>
            {
                Interlocked.Increment(ref Cancels); Cancelled.TrySetResult();
                if (BlockCancellation) CallbackRelease.Wait();
            });
        }

        private void InitializeUnsupported()
        {
            var domain = new ColdMetadataAuthority().CreateDomain();
            _injectedPreparation = domain.BeginPreparation(); var metadata = _injectedPreparation;
            try
            {
                Ensure(SharpLinkAssemblyManifestLoader.TryLoad(OldAssembly, metadata, out var manifest).Succeeded,
                    "real prepared manifest for metadata-only injection");
                metadata.ReserveCollection(8);
                var unsupported = DispatchProxy.Create<ISharpLinkClient, UnsupportedChild>();
                var slot = _injectedSlot = new SharpLinkClusterSlot(_key, unsupported, true, StaticManifests: [manifest!])
                    { MetadataOwner = metadata.Retain() };
                var contract = manifest!.Contracts.Single();
                var routes = new Dictionary<Type, SharpLinkClusterRouteRegistration>
                {
                    [contract.ContractType] = new(contract.ContractType, contract.ContractId, contract.Fingerprint, slot, OldAssembly)
                }.ToFrozenDictionary();
                _untransferredCoordinatorLease = metadata.Retain();
                _multi = new SharpLinkMultiClusterClient(new SharpLinkMultiClusterOptions(),
                    new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [_key] = slot }.ToFrozenDictionary(),
                    routes, [], metadataDomain: domain, metadataOwner: _untransferredCoordinatorLease, metadataPreparation: metadata);
                _untransferredCoordinatorLease = null;
                Read<List<DynamicAssemblyRegistration>>(_multi, "_dynamicRegistrations")
                    .Add(new DynamicAssemblyRegistration(slot, OldAssembly, manifest));
            }
            finally
            {
                // On success AND failed setup, storage retention is detached from
                // active preparation; Stop must not wait for this fixture itself.
                metadata.Complete();
            }
        }

        internal PluginAssembly NewAssembly(bool blockLoader = true, Exception? stubFailure = null, Exception? cleanupFailure = null)
        { var plugin = new PluginAssembly(blockLoader, stubFailure, cleanupFailure); _plugins.Add(plugin); return plugin; }
        internal Task<SharpLinkAssemblyReplacementResult> Replace(Assembly assembly, TimeSpan budget, CancellationToken token = default)
            => Own(_server is not null ? _server.ReplaceAssemblyAsync(OldAssembly, assembly, budget, token).AsTask()
                : _multi!.ReplaceAssemblyAsync(_key, OldAssembly, assembly, budget, token).AsTask());
        internal Task<SharpLinkAssemblyUnregisterResult> Unregister(TimeSpan budget, CancellationToken token = default)
            => Own(_server is not null ? _server.UnregisterAssemblyAsync(OldAssembly, budget, token).AsTask()
                : _multi!.UnregisterAssemblyAsync(_key, OldAssembly, budget, token).AsTask());
        internal void AcquireCall()
        { lock (_leaseGate) Ensure(!_call.IsAcquired && Module.TryAcquire(false, out _call), "actual module call admitted"); }
        internal void ReleaseCall()
        { lock (_leaseGate) if (_call.IsAcquired) { var lease = _call; _call = default; lease.Dispose(); } }
        internal RetirementGraceBudget Budget => Read<RetirementGraceBudget?>(Module.RetirementControl!.Driver, "_budget")!.Value;
        internal void AssertOldRunning() => Ensure(Module.State == SharpLinkDynamicModuleState.Running &&
            Module.RetirementOperation is null && Cancels == 0, "unpublished candidate leaves old module Running");
        internal void AssertPublished(Assembly replacement)
        {
            Ensure(Module.State is SharpLinkDynamicModuleState.Draining or SharpLinkDynamicModuleState.DrainTimedOut or
                SharpLinkDynamicModuleState.Released, "committed old admission cut");
            Ensure(IsChildRegistered(replacement), "actual child replacement published");
            if (_multi is not null) AssertRoute(replacement);
            CaptureModules();
        }
        internal bool IsChildRegistered(Assembly assembly)
        {
            if (_server is not null)
            {
                lock (Read<Lock>(_server, "_registryGate"))
                    return Read<ServerServiceModuleRegistry>(_server, "_serviceModuleRegistry").DynamicModules.ContainsKey(assembly);
            }
            lock (Read<Lock>(Registry, "_gate")) return Registry.DynamicModules.ContainsKey(assembly);
        }
        internal DynamicAssemblyRegistration Registration
        {
            get
            {
                lock (Read<Lock>(_multi!, "_gate"))
                    return Read<List<DynamicAssemblyRegistration>>(_multi!, "_dynamicRegistrations")
                        .Single(item => ReferenceEquals(item.Assembly, OldAssembly));
            }
        }
        internal bool HasRegistration(DynamicAssemblyRegistration registration)
        {
            lock (Read<Lock>(_multi!, "_gate"))
                return Read<List<DynamicAssemblyRegistration>>(_multi!, "_dynamicRegistrations")
                    .Any(item => ReferenceEquals(item, registration));
        }
        private T WithSnapshot<T>(Func<MultiClusterSnapshot, T> reader)
        {
            MultiClusterSnapshot snapshot;
            lock (Read<Lock>(_multi!, "_gate")) snapshot = Read<MultiClusterSnapshot>(_multi!, "_snapshot").Retain();
            try { return reader(snapshot); } finally { snapshot.Dispose(); }
        }
        internal bool HasRoute(Assembly assembly) => WithSnapshot(snapshot =>
            snapshot.Routes.Values.Any(route => ReferenceEquals(route.OwnerAssembly, assembly)));
        internal void AssertRoute(Assembly assembly) => Ensure(HasRoute(assembly), "actual route belongs to exact assembly");
        private void Remember(SharpLinkDynamicModule module)
        { lock (_tasksGate) if (!_modules.Any(existing => ReferenceEquals(existing, module))) _modules.Add(module); }
        private void CaptureModules()
        {
            if (_server is not null)
            {
                lock (Read<Lock>(_server, "_registryGate"))
                    foreach (var module in Read<ServerServiceModuleRegistry>(_server, "_serviceModuleRegistry").DynamicModules.Values) Remember(module);
            }
            foreach (var child in new[] { Child, FirstChild })
            {
                if (child is null) continue;
                var registry = Read<ClientAssemblyRegistry>(child, "_assemblyRegistry");
                lock (Read<Lock>(registry, "_gate")) foreach (var module in registry.DynamicModules.Values) Remember(module);
            }
        }
        internal async Task FinishRetirement()
        {
            ReleaseCall();
            var original = Module.RetirementOperation ?? throw new InvalidOperationException("No original physical task.");
            Own(original); Ensure((await Result(original)).ReferencesReleased, "actual physical release");
            var observer = Module.RetirementObservation ?? throw new InvalidOperationException("No original observer.");
            Own(observer); var result = await Result(observer);
            Ensure(result.Failure is null && Module.State == SharpLinkDynamicModuleState.Released,
                "original observer and module release, not inferred from outer Stop");
        }
        internal void ExpectPrivateCancellation(Exception failure, CancellationToken token)
        {
            Ensure(token.IsCancellationRequested && failure is OperationCanceledException &&
                Module.RetirementOperation!.IsCompletedSuccessfully && Module.State == SharpLinkDynamicModuleState.Released,
                "independent original success proves private cancellation provenance");
            Expected.Add(failure);
        }
        internal async Task<Exception?> Failure(Task original)
        {
            Task<Exception?> outcome;
            lock (_tasksGate)
            {
                if (!_outcomes.TryGetValue(original, out outcome!))
                { outcome = Observe(original); _outcomes.Add(original, outcome); }
            }
            Exception? diagnostic = null;
            try { await outcome.WaitAsync(Bound).ConfigureAwait(false); }
            catch (Exception exception) { diagnostic = exception; }
            if (diagnostic is not null)
            {
                if (outcome.IsCompletedSuccessfully && outcome.Result is { } source)
                    throw new AggregateException(diagnostic, source);
                ExceptionDispatchInfo.Capture(diagnostic).Throw();
            }
            return await outcome.ConfigureAwait(false);
        }
        private static async Task<Exception?> Observe(Task task)
        {
            try { await task.ConfigureAwait(false); return null; }
            catch (Exception exception) { return PhysicalTaskFailureException.GetRaw(exception); }
        }
        internal async Task Within(Task task)
        { if (await Failure(task) is { } failure) ExceptionDispatchInfo.Capture(failure).Throw(); }
        internal async Task<T> Result<T>(Task<T> task)
        { await Within(task); return await task.ConfigureAwait(false); }
        private bool IsExpected(Exception error)
        {
            if (Expected.Any(marker => ReferenceEquals(marker, error))) return true;
            if (error is PhysicalTaskFailureException) return IsExpected(PhysicalTaskFailureException.GetRaw(error));
            return error is AggregateException aggregate && aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsExpected);
        }
        private Task BeginStop(object host, Func<Task> stop)
        {
            lock (_tasksGate) if (!_stopping.Add(host)) return Task.CompletedTask;
            var invocation = Invoke(() =>
            {
                var original = Own(stop()); lock (_tasksGate) _hostStops.Add(host, original); return original;
            });
            return Own(JoinInvocation(invocation));
        }
        private static async Task JoinInvocation(Task<Task> invocation)
            => await (await invocation.ConfigureAwait(false)).ConfigureAwait(false);

        internal async Task Close(Exception? bodyFailure)
        {
            List<Exception> failures = [];
            if (bodyFailure is not null) failures.Add(bodyFailure);
            Clock.SampleRelease.Set(); FirstClock?.SampleRelease.Set(); CallbackRelease.Set();
            foreach (var plugin in _plugins) plugin.Release.TrySetResult();
            ReleaseCall();
            async Task Join(Task original, string stage)
            {
                Exception? source;
                try { source = await Failure(original); }
                catch (Exception diagnostic)
                { failures.Add(new InvalidOperationException(stage + ": diagnostic wait failed; original=" + original.Status, diagnostic)); return; }
                if (source is not null && !IsExpected(source)) failures.Add(new InvalidOperationException(stage + ": unexpected original failure", source));
            }
            if (_initialization is not null) await Join(_initialization, "initialization source");
            foreach (var task in Invocations) await Join(task, "invocation");
            // No host-set freeze until all potential creators have actually left.
            // A late completion cannot erase the diagnostic or its retained root.
            if ((_initialization is not null && !_initialization.IsCompleted) || Invocations.Any(static task => !task.IsCompleted))
            {
                failures.Add(new InvalidOperationException("creation still owns possible future hosts; retain fixture and permit"));
                throw new AggregateException(failures);
            }
            if (_multi is not null && (mode is Mode.Multi or Mode.SharedProvider) && !_childrenCaptured)
            {
                failures.Add(new InvalidOperationException("built coordinator child inventory is incomplete; retain fixture and permit"));
                throw new AggregateException(failures);
            }
            foreach (var queue in _queues)
            {
                try { await Dispatch(queue); }
                catch (Exception exception) { failures.Add(exception); }
            }
            CaptureModules();
            if (_server is not null) await Join(BeginStop(_server, () => _server.StopAsync(TimeSpan.Zero).AsTask()), "server stop");
            if (Child is not null) await Join(BeginStop(Child, () => Child.StopAsync().AsTask()), "selected child stop");
            if (FirstChild is not null) await Join(BeginStop(FirstChild, () => FirstChild.StopAsync().AsTask()), "first child stop");
            if (_multi is not null) await Join(BeginStop(_multi, () => _multi.StopAsync().AsTask()), "coordinator stop");
            CaptureModules();
            SharpLinkDynamicModule[] modules;
            lock (_tasksGate) modules = _modules.ToArray();
            foreach (var module in modules)
            {
                if (module.RetirementControl?.Driver.Task is { } driver) await Join(Own(driver), "driver");
                if (module.RetirementOperation is { } physical) await Join(Own(physical), "physical");
                if (module.RetirementObservation is { } observer)
                {
                    await Join(Own(observer), "observer");
                    if (observer.IsCompletedSuccessfully && observer.Result.Failure is { } failure && !IsExpected(failure))
                        failures.Add(new InvalidOperationException("observer data failure", failure));
                }
                if (module.State != SharpLinkDynamicModuleState.Released)
                    failures.Add(new InvalidOperationException("normal control retained an unreleased module"));
            }
            if (_server is not null)
            {
                var lifecycle = Read<SharpLinkServer.ServerLifecycleCoordinator>(_server, "_lifecycle");
                if (lifecycle.DeferredServiceCleanupTaskForDiagnostics is { } physical) await Join(Own(physical), "service physical");
                if (lifecycle.ShutdownCleanupObserverTaskForDiagnostics is { } observer) await Join(Own(observer), "shutdown observer");
            }
            foreach (var task in Tasks) await Join(task, "owned source");
            await Join(Clock.Inner.WaitForTimersDrainedAsync(), "selected provider timers");
            if (FirstClock is not null && !ReferenceEquals(Clock, FirstClock))
                await Join(FirstClock.Inner.WaitForTimersDrainedAsync(), "first provider timers");
            var hosts = new object?[] { _server, Child, FirstChild, _multi }.Where(static host => host is not null)
                .Cast<object>().Distinct(ReferenceEqualityComparer.Instance).ToArray();
            bool stopped;
            lock (_tasksGate) stopped = hosts.All(host => _hostStops.TryGetValue(host, out var original) && original.IsCompletedSuccessfully);
            var runtimeReleased = new object?[] { _server, Child, FirstChild }.Where(static host => host is not null)
                .All(host => RuntimeReleased(Read<SharpLinkRuntimeContext>(host!, "_runtimeContext")));
            var ended = stopped && runtimeReleased && Tasks.All(static task => task.IsCompleted) && Outcomes.All(static task => task.IsCompleted) &&
                _queues.All(static queue => queue.Pending == 0) && Clock.Inner.ActiveTimerCount == 0 &&
                (FirstClock?.Inner.ActiveTimerCount ?? 0) == 0 && modules.All(static module =>
                    module.State == SharpLinkDynamicModuleState.Released &&
                    module.RetirementOperation is { IsCompletedSuccessfully: true } physical && physical.Result.ReferencesReleased &&
                    module.RetirementObservation is { IsCompletedSuccessfully: true } observer && observer.Result.Failure is null &&
                    module.RetirementControl?.Driver.Task is { IsCompletedSuccessfully: true } driver && !driver.Result.HasRetainedTimer);
            if (ended)
            {
                _cancellation.Dispose();
                foreach (var plugin in _plugins) plugin.Dispose();
                if (_multi is null) _injectedSlot?.ReleaseMetadata();
                _untransferredCoordinatorLease?.Dispose(); _injectedPreparation?.Dispose();
                CallbackRelease.Dispose(); Clock.SampleRelease.Dispose();
                if (FirstClock is not null && !ReferenceEquals(Clock, FirstClock)) FirstClock.SampleRelease.Dispose();
                OwnersEnded = true;
            }
            if (failures.Count != 0) throw new AggregateException(failures);
            Ensure(OwnersEnded, "actual source/physical/module/accounting exit proof required before isolation refund");
        }
    }

    private static bool RuntimeReleased(SharpLinkRuntimeContext context)
        => Read<int>(context, "_disposed") != 0 && Read<object?>(context, "_metadataOwner") is null &&
            Read<object?>(context, "_publicationMetadataOwner") is null && Read<object?>(context, "_generatedManifests") is null;

    private sealed class PluginAssembly : IDisposable
    {
        private readonly AssemblyLoadContext _context = new("entry-control-" + Guid.NewGuid(), isCollectible: true);
        private readonly Type _state;
        internal readonly TaskCompletionSource Entered = Signal();
        internal readonly TaskCompletionSource Release = Signal();
        internal Assembly Assembly { get; }
        internal PluginAssembly(bool block, Exception? stubFailure, Exception? cleanupFailure)
        {
            try
            {
                Assembly = _context.LoadFromAssemblyPath(typeof(EntryManifest).Assembly.Location);
                _state = Assembly.GetType(typeof(EntryState).FullName!, throwOnError: true)!;
                Set(nameof(EntryState.ManifestEntered), Entered); Set(nameof(EntryState.ManifestRelease), Release);
                Set(nameof(EntryState.StubFailure), stubFailure); Set(nameof(EntryState.ScopeFailure), cleanupFailure);
                if (!block) Release.TrySetResult();
            }
            catch { _context.Unload(); throw; }
        }
        private void Set(string name, object? value) => _state.GetField(name)!.SetValue(null, value);
        internal int Count(string name) => (int)_state.GetField(name)!.GetValue(null)!;
        internal Exception? ManifestFailureObserved => (Exception?)_state.GetField(nameof(EntryState.ManifestFailureObserved))!.GetValue(null);
        public void Dispose()
        {
            Set(nameof(EntryState.ManifestEntered), null); Set(nameof(EntryState.ManifestRelease), null);
            Set(nameof(EntryState.StubFailure), null); Set(nameof(EntryState.ScopeFailure), null);
            Set(nameof(EntryState.ManifestFailureObserved), null); _context.Unload();
        }
    }

    private sealed class StageClock(long frequency, long origin) : TimeProvider
    {
        internal readonly ManualTimeProvider Inner = new();
        internal readonly TaskCompletionSource Armed = Signal();
        internal readonly TaskCompletionSource SampleEntered = Signal();
        internal readonly ManualResetEventSlim SampleRelease = new(false);
        private int _sampleMode;
        private int _sampleThread;
        private Exception? _sampleFailure;
        internal int FailedSamples;
        internal long Peek => unchecked(origin + (long)((Int128)Inner.GetTimestamp() * frequency / TimeSpan.TicksPerSecond));
        public override long TimestampFrequency => frequency;
        public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow();
        internal void BlockNextSample() => Volatile.Write(ref _sampleMode, 1);
        internal void FailNextSample(Exception error) { _sampleFailure = error; Volatile.Write(ref _sampleMode, 2); }
        internal void ClaimSampleHook()
        {
            if (Volatile.Read(ref _sampleMode) != 0)
                Interlocked.CompareExchange(ref _sampleThread, Environment.CurrentManagedThreadId, 0);
        }
        public override long GetTimestamp()
        {
            var mode = Environment.CurrentManagedThreadId == Volatile.Read(ref _sampleThread)
                ? Interlocked.Exchange(ref _sampleMode, 0) : 0;
            var sample = Peek;
            if (mode != 0)
            {
                SampleEntered.TrySetResult();
                if (mode == 1) SampleRelease.Wait();
                else
                {
                    Interlocked.Increment(ref FailedSamples);
                    ExceptionDispatchInfo.Capture(_sampleFailure!).Throw();
                }
            }
            return sample;
        }
        internal void Advance(TimeSpan elapsed) => Inner.Advance(elapsed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Inner.CreateTimer(callback, state, dueTime, period);
            return state is RetirementCancellationDriver ? new DriverTimer(this, timer) : timer;
        }
        private sealed class DriverTimer(StageClock owner, ITimer timer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { var result = timer.Change(dueTime, period); owner.Armed.TrySetResult(); return result; }
            public void Dispose() => timer.Dispose();
            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }

    private sealed class OwnedQueue : SynchronizationContext
    {
        private readonly Lock _gate = new();
        private readonly Queue<Work> _queue = new();
        private int _running;
        internal readonly TaskCompletionSource Posted = Signal();
        internal int Pending { get { lock (_gate) return _queue.Count + _running; } }
        public override void Post(SendOrPostCallback callback, object? state)
        { lock (_gate) _queue.Enqueue(new Work(callback, state)); Posted.TrySetResult(); }
        internal bool TryTake(out Work item)
        {
            lock (_gate)
            {
                if (!_queue.TryDequeue(out item)) return false;
                _running++; return true;
            }
        }
        internal void CompleteDispatch() { lock (_gate) _running--; }
        internal readonly record struct Work(SendOrPostCallback Callback, object? State);
    }

    // Deliberately metadata-only injected capability boundary, not fake runtime proof.
    public class UnsupportedChild : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "StopAsync" or "DisposeAsync" => ValueTask.CompletedTask,
                "get_LifecycleState" => SharpLinkClientLifecycleState.Running,
                "get_Readiness" => SharpLinkReadinessState.Ready,
                "get_State" => SharpLinkConnectionState.Ready,
                "get_ClusterState" => SharpLinkClusterState.Ready,
                _ => throw new NotSupportedException("Metadata-only unsupported child: " + targetMethod?.Name)
            };
    }
    private sealed class Listener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException("unreachable"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Connector : IClientTransportFactory
    {
        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException("unreachable"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Read<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing field " + name);
        return (T)field.GetValue(instance)!;
    }
    private static int Count(Exception? error, Exception marker)
        => ReferenceEquals(error, marker) ? 1 : error is AggregateException aggregate
            ? aggregate.InnerExceptions.Sum(inner => Count(inner, marker))
            : error is PhysicalTaskFailureException ? Count(PhysicalTaskFailureException.GetRaw(error), marker) : 0;
    private static bool Only(Exception error, Exception marker)
        => ReferenceEquals(error, marker) || error is AggregateException aggregate && aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(inner => Only(inner, marker)) ||
            error is PhysicalTaskFailureException && Only(PhysicalTaskFailureException.GetRaw(error), marker);
    private static void Ensure(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
