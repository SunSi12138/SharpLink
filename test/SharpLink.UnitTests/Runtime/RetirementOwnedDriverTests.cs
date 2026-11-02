using System.Net;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using SharpLink.Client;
using SharpLink.RollbackPlugin;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

// These controls use a real current-ABI manifest, real started hosts, the actual
// elected module/physical/observer tasks, and finite outer diagnostic waits.
// A failed diagnostic wait never becomes a replacement physical completion.
[NotInParallel("rollback-plugin")]
public sealed class RetirementOwnedDriverTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly List<Fixture> Containment = [];

    [Test]
    public Task ClientRepeatedWaiterCannotRestartTheFirstEntryBudget()
        => Run(false, RepeatedWaiterCannotRestartTheFirstEntryBudget);

    [Test]
    public Task ServerRepeatedWaiterCannotRestartTheFirstEntryBudget()
        => Run(true, RepeatedWaiterCannotRestartTheFirstEntryBudget);

    private static async Task RepeatedWaiterCannotRestartTheFirstEntryBudget(Fixture f)
    {
        f.AcquireCall();
        var cancellation = Signal();
        f.RegisterCancellation(() => cancellation.TrySetResult());
        var first = f.Unregister(TimeSpan.FromSeconds(5));
        await f.Clock.Armed.Task.WaitAsync(Bound);
        f.Clock.Advance(TimeSpan.FromSeconds(4));
        var repeated = f.Unregister(TimeSpan.FromDays(1));
        Ensure(!first.IsCompleted && !repeated.IsCompleted, "both original waiters still await first budget");
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await cancellation.Task.WaitAsync(Bound);
        Ensure(!(await first.WaitAsync(Bound)).ReferencesReleased &&
            !(await repeated.WaitAsync(Bound)).ReferencesReleased, "both observe elected five-second budget");
        Ensure(f.CancellationCount == 1, "one cancellation driver");
        f.ReleaseCall();
        await f.Module.RetirementOperation!.WaitAsync(Bound);
        await f.Module.RetirementObservation!.WaitAsync(Bound);
        Ensure(f.Module.State == SharpLinkDynamicModuleState.Released, "physical owner actually released");
    }

    [Test]
    public Task ProviderBlockedAfterElectionDoesNotOwnThePublicWaiterOrResetEntry()
        => Run(true, async f =>
        {
            f.AcquireCall();
            f.Clock.BlockCreate = true;
            var cancelled = Signal();
            f.RegisterCancellation(() => cancelled.TrySetResult());
            using var caller = new CancellationTokenSource();
            var invocation = f.Invoke(() => f.Unregister(TimeSpan.FromSeconds(5), caller.Token));
            var waiter = await invocation.WaitAsync(Bound);
            await f.Clock.CreateEntered.Task.WaitAsync(Bound);
            caller.Cancel();
            var waitFailure = await f.Failure(waiter);
            Ensure(waitFailure is OperationCanceledException && !f.Module.RetirementOperation!.IsCompleted,
                "returned public waiter can leave while synchronous provider remains blocked");
            f.ExpectedFailures.Add(waitFailure!);
            Ensure(f.CancellationCount == 0, "blocked provider does not promise punctual cancellation");
            f.Clock.Advance(TimeSpan.FromSeconds(5));
            f.Clock.CreateRelease.Set();
            await cancelled.Task.WaitAsync(Bound);
            Ensure(f.CancellationCount == 1, "unblocked driver consumes entry budget instead of restarting");
            f.ReleaseCall();
            await f.Module.RetirementOperation!.WaitAsync(Bound);
        });

    [Test]
    public Task BlockedCancellationCallbackAllowsCallerAbandonmentButRetainsPhysicalOwner()
        => Run(true, async f =>
        {
            f.AcquireCall();
            var entered = Signal();
            f.RegisterCancellation(() => { entered.TrySetResult(); f.CallbackRelease.Wait(); });
            using var caller = new CancellationTokenSource();
            // Preserve invocation itself as an owner: a synchronous-callback
            // regression must fail the bound rather than hang the test thread.
            var invocation = f.Invoke(() => f.Unregister(TimeSpan.Zero, caller.Token));
            var waiter = await invocation.WaitAsync(Bound);
            await entered.Task.WaitAsync(Bound);
            caller.Cancel();
            var waitFailure = await f.Failure(waiter);
            Ensure(waitFailure is OperationCanceledException, "caller owns only its wait");
            f.ExpectedFailures.Add(waitFailure!);
            f.ReleaseCall();
            Ensure(!f.Module.RetirementOperation!.IsCompleted && !f.Module.RetirementControl!.Driver.Task.IsCompleted,
                "zero calls is not proof that an executing cancellation callback exited");
            var stop = f.Stop();
            f.CallbackRelease.Set();
            await f.Module.RetirementOperation.WaitAsync(Bound);
            await stop.WaitAsync(Bound);
            Ensure(f.CancellationCount == 1, "Stop signals force, never enters a second CTS.Cancel");
        });

    [Test]
    public Task ZeroCallsWithPreparationDoesNotInventTimeoutOrWholeModuleDrain()
        => Run(true, async f =>
        {
            f.AcquirePreparation();
            f.RegisterCancellation(static () => { });
            using var caller = new CancellationTokenSource();
            var waiter = f.Unregister(TimeSpan.Zero, caller.Token);
            var driver = await f.Module.RetirementControl!.Driver.Task.WaitAsync(Bound);
            Ensure(!driver.CancellationAttempted && f.CancellationCount == 0,
                "preparation alone is not a remaining call");
            Ensure(f.Module.State == SharpLinkDynamicModuleState.Draining && !waiter.IsCompleted &&
                !f.Module.WaitForDrainAsync().IsCompleted, "no false timeout or whole-module drained completion");
            caller.Cancel();
            var waitFailure = await f.Failure(waiter);
            Ensure(waitFailure is OperationCanceledException, "preparation wait is caller-cancellable");
            f.ExpectedFailures.Add(waitFailure!);
            f.ReleasePreparation();
            Ensure((await f.Module.RetirementOperation!.WaitAsync(Bound)).ReferencesReleased,
                "actual preparation exit allows actual physical cleanup");
        });

    [Test]
    public Task ProviderArmingFailureCancelsRemainingCallAndPreservesTheRawOccurrence()
        => Run(true, async f =>
        {
            var original = new OperationCanceledException("provider create failure, not caller cancellation");
            f.ExpectedFailures.Add(original);
            f.Clock.CreateFailure = original;
            f.AcquireCall();
            var entered = Signal();
            f.RegisterCancellation(() => entered.TrySetResult());
            var waiter = f.Unregister(TimeSpan.FromSeconds(5));
            await entered.Task.WaitAsync(Bound);
            f.ReleaseCall();
            Ensure(Count(await f.Failure(waiter), original) == 1, "raw provider OCE is source failure exactly once");
            Ensure(f.Module.State == SharpLinkDynamicModuleState.Released && f.CancellationCount == 1,
                "fail-closed cancellation still joins real call and cleanup");
        });

    [Test]
    public Task FailedTimerDisposalRetainsRealOwnerAndDoesNotAdvanceRuntimeCleanup()
        => Run(true, async f =>
        {
            var original = new InvalidOperationException("unknown timer exit");
            f.ExpectedFailures.Add(original);
            f.Quarantine = true;
            f.Clock.DisposeFailure = original;
            f.AcquireCall();
            var entered = Signal();
            f.RegisterCancellation(() => entered.TrySetResult());
            var waiter = f.Unregister(TimeSpan.FromSeconds(1));
            await f.Clock.Armed.Task.WaitAsync(Bound);
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            await entered.Task.WaitAsync(Bound);
            f.ReleaseCall();
            Ensure(Count(await f.Failure(waiter), original) == 1, "timer fault remains its original occurrence");
            var control = f.Module.RetirementControl!;
            var outcome = await control.Driver.Task.WaitAsync(Bound);
            Ensure(outcome.HasRetainedTimer && f.Clock.LastOwnedTimer is not null &&
                ReferenceEquals(Read<ITimer>(control.Driver, "_ownedTimer"), f.Clock.LastOwnedTimer),
                "actual timer handle, not merely failed-task boolean, remains rooted");
            Ensure(f.Module.State != SharpLinkDynamicModuleState.Released && f.IsRegistered(),
                "unknown timer exit blocks module release");
            var reservation = Read<ColdMetadataReservation>(f.Module, "_retirementCapacity");
            var retainedBytes = Read<ulong>(reservation, "_bytes");
            await f.VerifyRetainedStopFailure();
            Ensure(f.IsRegistered() && Read<int>(f.Context, "_disposed") == 0 &&
                ReferenceEquals(reservation, Read<ColdMetadataReservation>(f.Module, "_retirementCapacity")) &&
                Read<ulong>(reservation, "_bytes") == retainedBytes && retainedBytes != 0 &&
                Read<int>(reservation, "_references") >= 2,
                "Faulted host still roots registry/module/storage and does not refund original or observer lease");
            f.AllowQuarantineFakeEnd = true;
            Ensure(f.Clock.DisposeCalls == 1, "production never retries arbitrary provider DisposeAsync");
            // Harness-only terminalization of this known fake. This is not a
            // production recovery mechanism and does not release module quota.
            await f.Clock.EndKnownFakeOutOfBand();
            Ensure(f.Module.State != SharpLinkDynamicModuleState.Released, "fake exit does not fabricate release");
        });

    [Test]
    public Task LateWaiterMustObserveOriginalCleanupFailureInsteadOfCachedDeferredResult()
        => Run(true, async f =>
        {
            f.AcquireCall();
            var deferred = await f.Unregister(TimeSpan.Zero).WaitAsync(Bound);
            Ensure(!deferred.ReferencesReleased && !f.Module.RetirementOperation!.IsCompleted,
                "first waiter observes deferred call drain, not cleanup completion");
            // Existing cleanup still performs cold admission. Exhaust logical
            // authority without allocating a giant array, after driver exit.
            f.HoldCleanupQuota();
            f.Quarantine = true;
            f.ReleaseCall();
            var original = await f.Failure(f.Module.RetirementOperation!);
            Ensure(original is ColdMetadataException { Code: SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded } &&
                f.Module.State != SharpLinkDynamicModuleState.Released && f.Module.CleanupCompletion is null,
                "exact cold quota failure occurred before physical cleanup claim, not an arbitrary downstream error");
            f.ExpectedFailures.Add(original!);
            await f.Module.RetirementObservation!.WaitAsync(Bound);
            using var lateCaller = new CancellationTokenSource();
            lateCaller.Cancel();
            var late = await f.Failure(f.Unregister(TimeSpan.FromDays(1), lateCaller.Token));
            Ensure(ReferenceEquals(late, original),
                "terminal original wins even with cancelled caller; never stale deferred cache");
            f.ReleaseCleanupQuota();
            await f.VerifyRetainedStopFailure();
        });

    [Test]
    public Task SynchronousTimerHintsCannotExpirePositiveBudgetAndLateHintsAreHarmless()
        => Run(true, async f =>
        {
            f.Clock.SynchronousHints = true;
            f.AcquireCall();
            var entered = Signal();
            f.RegisterCancellation(() => entered.TrySetResult());
            var waiter = f.Unregister(TimeSpan.FromSeconds(1));
            await f.Clock.Armed.Task.WaitAsync(Bound);
            Ensure(f.CancellationCount == 0 && !waiter.IsCompleted,
                "Create/Change callback hints are not timestamp expiry");
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            await entered.Task.WaitAsync(Bound);
            Ensure(!(await waiter.WaitAsync(Bound)).ReferencesReleased, "real equality authorizes cancellation");
            f.ReleaseCall();
            await f.Module.RetirementOperation!.WaitAsync(Bound);
            f.Clock.LateHint();
            Ensure(f.CancellationCount == 1, "late old hint after Closing cannot touch disposed wake or cancel again");
        });

    [Test]
    public Task IndependentCancellationAndTimerStagesKeepTwoOccurrencesOfTheSameRawObject()
        => Run(true, async f =>
        {
            var shared = new OperationCanceledException("same object, independent occurrences");
            f.ExpectedFailures.Add(shared);
            f.Quarantine = true;
            f.Clock.DisposeFailure = shared;
            f.AcquireCall();
            var entered = Signal();
            f.RegisterCancellation(() => { entered.TrySetResult(); throw shared; });
            var waiter = f.Unregister(TimeSpan.FromSeconds(1));
            await f.Clock.Armed.Task.WaitAsync(Bound);
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            await entered.Task.WaitAsync(Bound);
            f.ReleaseCall();
            var original = await f.Failure(waiter);
            Ensure(Count(original, shared) == 2, "CTS callback and timer teardown are distinct occurrences");
            var driver = await f.Module.RetirementControl!.Driver.Task.WaitAsync(Bound);
            Ensure(Count(driver.CancellationFailure, shared) == 1 && ReferenceEquals(driver.TimerFailure, shared),
                "driver retains separate raw stage results without reference/token/type deduplication");
            Ensure(driver.HasRetainedTimer && ReferenceEquals(
                Read<ITimer>(f.Module.RetirementControl.Driver, "_ownedTimer"), f.Clock.LastOwnedTimer),
                "same-object failure control retains its actual unknown-exit timer");
            await f.VerifyRetainedStopFailure();
            f.AllowQuarantineFakeEnd = true;
        });

    [Test]
    public Task ReplacementPreparationConsumesEntryBudgetButCannotCancelBeforePublication()
        => Run(true, async f =>
        {
            f.AcquireCall();
            var cancelled = Signal();
            f.RegisterCancellation(() => cancelled.TrySetResult());
            var replacement = f.PrepareReplacementAssembly();
            var invocation = f.Invoke(() => f.Replace(replacement.Assembly, TimeSpan.FromSeconds(5)));
            await replacement.Entered.Task.WaitAsync(Bound);
            f.Clock.Advance(TimeSpan.FromSeconds(5));
            Ensure(f.Module.State == SharpLinkDynamicModuleState.Running && f.CancellationCount == 0 &&
                f.Module.RetirementOperation is null, "slow preparation has not authorized old-module retirement");
            replacement.Release.TrySetResult();
            var waiter = await invocation.WaitAsync(Bound);
            await cancelled.Task.WaitAsync(Bound);
            var result = await waiter.WaitAsync(Bound);
            Ensure(result.Succeeded && !result.ReferencesReleased && f.CancellationCount == 1,
                "successful publication consumes the original five seconds immediately");
            f.ReleaseCall();
            await f.Module.RetirementOperation!.WaitAsync(Bound);
        });

    [Test]
    public Task FailedReplacementPreparationLeavesOldModuleRunningWithoutCancellation()
        => Run(true, async f =>
        {
            f.AcquireCall();
            f.RegisterCancellation(static () => { });
            var replacement = f.PrepareReplacementAssembly();
            var invocation = f.Invoke(() => f.Replace(replacement.Assembly, TimeSpan.Zero));
            await replacement.Entered.Task.WaitAsync(Bound);
            replacement.Release.TrySetException(new InvalidOperationException("test preparation rejection"));
            var result = await (await invocation.WaitAsync(Bound)).WaitAsync(Bound);
            Ensure(!result.Succeeded && f.Module.State == SharpLinkDynamicModuleState.Running &&
                f.Module.RetirementOperation is null && f.CancellationCount == 0,
                "zero budget cannot cancel old module when new preparation failed");
            f.ReleaseCall();
            Ensure((await f.Unregister(TimeSpan.Zero).WaitAsync(Bound)).ReferencesReleased,
                "old owner remains usable for a later independent retirement");
        });

    [Test]
    public Task PostcommitNotificationIsOwnedOffThePublicThreadAndCallerCanStopWaiting()
        => Run(false, async f =>
        {
            f.AcquireCall();
            var notification = Signal();
            f.SetLocalRetirementCallback(() => { notification.TrySetResult(); f.CallbackRelease.Wait(); });
            var cancelled = Signal();
            f.RegisterCancellation(() => cancelled.TrySetResult());
            using var caller = new CancellationTokenSource();
            var invocation = f.Invoke(() => f.Unregister(TimeSpan.Zero, caller.Token));
            var waiter = await invocation.WaitAsync(Bound);
            await notification.Task.WaitAsync(Bound);
            await cancelled.Task.WaitAsync(Bound);
            Ensure(!f.Module.RetirementControl!.Ready.Task.IsCompleted,
                "blocked notification remains an unfinished physical phase");
            caller.Cancel();
            var waitFailure = await f.Failure(waiter);
            Ensure(waitFailure is OperationCanceledException, "caller exits while real postcommit callback remains blocked");
            f.ExpectedFailures.Add(waitFailure!);
            f.ReleaseCall();
            Ensure(!f.Module.RetirementOperation!.IsCompleted,
                "independent completed driver cannot release a blocked postcommit owner");
            f.CallbackRelease.Set();
            Ensure((await f.Module.RetirementOperation.WaitAsync(Bound)).ReferencesReleased,
                "physical release occurs after actual notification exit");
        }, withContract: true);

    [Test]
    public Task RacingCallerCancellationAndRealCallExitLeaveOriginalOwnerIntact()
        => Run(true, async f =>
        {
            f.AcquireCall();
            using var caller = new CancellationTokenSource();
            var waiter = f.Unregister(TimeSpan.FromDays(1), caller.Token);
            await f.Clock.Armed.Task.WaitAsync(Bound);
            var original = f.Module.RetirementOperation!;
            var cancelReady = Signal(); var exitReady = Signal();
            var cancel = f.Invoke(() =>
            { cancelReady.TrySetResult(); f.CallbackRelease.Wait(); caller.Cancel(); return true; });
            var exit = f.Invoke(() =>
            { exitReady.TrySetResult(); f.CallbackRelease.Wait(); f.ReleaseCall(); return true; });
            await Task.WhenAll(cancelReady.Task, exitReady.Task).WaitAsync(Bound);
            f.CallbackRelease.Set();
            await Task.WhenAll(cancel, exit).WaitAsync(Bound);
            var waiterFailure = await f.Failure(waiter);
            // Either racing wait result is legal; prove its source independently
            // from the actual original owner, never from OCE/token/ref heuristics.
            Ensure((await original.WaitAsync(Bound)).ReferencesReleased && original.IsCompletedSuccessfully,
                "real source independently proves successful physical completion");
            if (waiterFailure is not null)
            {
                Ensure(caller.IsCancellationRequested && waiterFailure is OperationCanceledException,
                    "only private waiter cancellation remains after independently successful source");
                f.ExpectedFailures.Add(waiterFailure);
            }
            else Ensure((await waiter).ReferencesReleased, "source-winning wait returns actual release");
            await f.Module.RetirementObservation!.WaitAsync(Bound);
        });

    private static async Task Run(bool server, Func<Fixture, Task> body, bool enableCodec = false, bool withContract = false)
    {
        Ensure(await RollbackState.TestIsolation.WaitAsync(Bound), "finite test isolation admission");
        var previous = Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC");
        var previousContract = Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_RETIREMENT_CONTRACT");
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_RETIREMENT_CONTRACT", withContract ? "1" : null);
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", enableCodec ? null : "1");
        var f = new Fixture(server);
        Exception? failure = null;
        try { await f.Initialize(); await body(f); }
        catch (Exception exception) { failure = exception; }
        try { await f.Close(failure); }
        finally
        {
            // Unknown pending work keeps BOTH roots and shared-state isolation.
            // Later controls fail finite admission instead of racing this owner;
            // the selected test process must then be contained by its runner.
            if (!f.OwnersEnded)
            {
                lock (Containment)
                    if (!Containment.Contains(f)) Containment.Add(f);
            }
            else
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", previous);
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_RETIREMENT_CONTRACT", previousContract);
                RollbackState.TestIsolation.Release();
            }
        }
    }

    private sealed class Fixture(bool server)
    {
        internal readonly ControlledProvider Clock = new();
        internal readonly ManualResetEventSlim CallbackRelease = new(false);
        internal readonly List<Exception> ExpectedFailures = [];
        internal SharpLinkDynamicModule Module = null!;
        internal SharpLinkRuntimeContext Context = null!;
        internal bool OwnersEnded;
        internal bool Quarantine;
        internal bool AllowQuarantineFakeEnd;
        private readonly Lock _tasksGate = new();
        private readonly List<Task> _invocations = [];
        private readonly Dictionary<Task, Task<Exception?>> _outcomes = new(ReferenceEqualityComparer.Instance);
        private Task[] Outcomes { get { lock (_tasksGate) return _outcomes.Values.Cast<Task>().ToArray(); } }
        private Task[] Invocations { get { lock (_tasksGate) return _invocations.ToArray(); } }
        private Task[] Waiters { get { lock (_tasksGate) return _waiters.ToArray(); } }
        internal Task<T> Invoke<T>(Func<T> invoke)
        {
            var invocation = Task.Factory.StartNew(invoke, CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            lock (_tasksGate) _invocations.Add(invocation);
            return invocation;
        }
        internal int CancellationCount;
        private SharpLinkClient? _client;
        private SharpLinkServer? _server;
        private readonly Lock _leaseGate = new();
        private SharpLinkDynamicModuleLease _lease;
        private bool _preparation;
        private CancellationTokenRegistration _cancellation;
        private Task? _stop;
        private Task? _hostStop;
        private readonly List<Task> _waiters = [];
        private ReplacementAssembly? _replacement;
        internal ReplacementAssembly PrepareReplacementAssembly()
            => _replacement = new ReplacementAssembly();
        internal Task<SharpLinkAssemblyReplacementResult> Replace(Assembly replacement, TimeSpan grace)
        {
            var waiter = _server!.ReplaceAssemblyAsync(_assembly, replacement, grace).AsTask();
            lock (_tasksGate) _waiters.Add(waiter);
            return waiter;
        }
        private ColdMetadataPreparation? _quotaBlocker;
        internal void HoldCleanupQuota()
        {
            _quotaBlocker = Context.CreateMetadataStorage();
            var available = ColdMetadataAuthority.AuthorityByteLimit - Context.Domain.Authority.Bytes;
            var slots = checked((int)((available - ColdMetadataAuthority.HeaderCharge) / ColdMetadataAuthority.SlotCharge));
            _quotaBlocker.ReserveCollection(slots);
        }
        internal void ReleaseCleanupQuota() => Interlocked.Exchange(ref _quotaBlocker, null)?.Dispose();
        private readonly Assembly _assembly = typeof(RollbackMarker).Assembly;

        internal async Task Initialize()
        {
            // RollbackManifest constructs a real nonzero identity hash even with
            // codecs disabled; no fabricated empty manifest/hash is injected.
            if (server)
            {
                _server = (SharpLinkServer)SharpLinkServerBuilder.Create()
                    .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                    .UseTimeProvider(Clock).UseTransport(new Listener()).Build();
                await _server.StartAsync().AsTask().WaitAsync(Bound);
                Ensure(_server.LifecycleState == SharpLinkServerLifecycleState.Running, "actual Server Start");
                Ensure(_server.RegisterAssembly(_assembly).Succeeded, "real dynamic Server publication");
                Module = Read<ServerServiceModuleRegistry>(_server, "_serviceModuleRegistry").DynamicModules
                    .Single(pair => ReferenceEquals(pair.Key, _assembly)).Value;
                Context = Read<SharpLinkRuntimeContext>(_server, "_runtimeContext");
            }
            else
            {
                _client = (SharpLinkClient)SharpClientBuilder.Create()
                    .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).DisableRequestTimeout()
                    .UseTimeProvider(Clock).UseTransport(new Connector()).Build();
                await _client.StartAsync().AsTask().WaitAsync(Bound);
                Ensure(_client.LifecycleState == SharpLinkClientLifecycleState.Running, "actual Client Start");
                Ensure(_client.RegisterAssembly(_assembly).Succeeded, "real dynamic Client publication");
                Module = Read<ClientAssemblyRegistry>(_client, "_assemblyRegistry").DynamicModules[_assembly];
                Context = Read<SharpLinkRuntimeContext>(_client, "_runtimeContext");
            }
        }

        internal Task<SharpLinkAssemblyUnregisterResult> Unregister(TimeSpan grace, CancellationToken token = default)
        {
            var waiter = _server is not null ? _server.UnregisterAssemblyAsync(_assembly, grace, token).AsTask()
                : _client!.UnregisterAssemblyAsync(_assembly, grace, token).AsTask();
            lock (_tasksGate) _waiters.Add(waiter);
            return waiter;
        }
        internal SharpLinkServer.ServerLifecycleCoordinator? ServerLifecycle => _server is null ? null
            : Read<SharpLinkServer.ServerLifecycleCoordinator>(_server, "_lifecycle");
        internal Task Stop() => _stop ??= JoinStopInvocationAsync(Invoke(() =>
            _hostStop = _server is not null ? _server.StopAsync(TimeSpan.Zero).AsTask() : _client!.StopAsync().AsTask()));
        private static async Task JoinStopInvocationAsync(Task<Task> invocation)
            => await (await invocation.ConfigureAwait(false)).ConfigureAwait(false);
        internal bool IsRegistered() => _server is not null
            ? Read<ServerServiceModuleRegistry>(_server, "_serviceModuleRegistry").DynamicModules.ContainsKey(_assembly)
            : Read<ClientAssemblyRegistry>(_client!, "_assemblyRegistry").DynamicModules.ContainsKey(_assembly);
        internal void AcquireCall()
        { lock (_leaseGate) Ensure(Module.TryAcquire(false, out _lease), "genuine admitted module call"); }
        internal void ReleaseCall()
        {
            lock (_leaseGate)
                if (_lease.IsAcquired) { var lease = _lease; _lease = default; lease.Dispose(); }
        }
        internal void AcquirePreparation()
        { lock (_leaseGate) { Ensure(Module.TryAcquirePreparation(), "real preparation pin"); _preparation = true; } }
        internal void ReleasePreparation()
        { lock (_leaseGate) if (_preparation) { _preparation = false; Module.ReleasePreparation(); } }
        internal void RegisterCancellation(Action action) => _cancellation = Module.ForcedCancellation.Register(() =>
        { Interlocked.Increment(ref CancellationCount); action(); });
        internal void SetLocalRetirementCallback(Action action)
        {
            var registry = Read<ClientAssemblyRegistry>(_client!, "_assemblyRegistry");
            var lookup = Read<ClientProxyLookup>(registry, "_proxyLookup");
            var registration = lookup.Capture().Values.Single(item => ReferenceEquals(item.Module, Module));
            registration.LocalControl.SetRetirementCallback(_ => action());
        }


        internal async Task<Exception?> Failure(Task original)
        {
            Task<Exception?> outcome;
            lock (_tasksGate)
            {
                if (!_outcomes.TryGetValue(original, out outcome!))
                {
                    outcome = ObserveOriginalOutcomeAsync(original);
                    _outcomes.Add(original, outcome);
                }
            }
            // Original faults (including TimeoutException/OCE) are DATA on this
            // cached observer. This catch belongs exclusively to the diagnostic
            // wait, never to a user source exception or its cancellation token.
            Exception? diagnosticFailure = null;
            try { await outcome.WaitAsync(Bound).ConfigureAwait(false); }
            catch (Exception diagnostic) { diagnosticFailure = diagnostic; }
            if (diagnosticFailure is not null)
            {
                // A late completion may provide an additional source occurrence;
                // it can never erase the diagnostic failure that already happened.
                if (outcome.IsCompletedSuccessfully && outcome.Result is { } source)
                    throw new AggregateException(diagnosticFailure, source);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(diagnosticFailure).Throw();
            }
            return await outcome.ConfigureAwait(false);
        }

        private static async Task<Exception?> ObserveOriginalOutcomeAsync(Task original)
        {
            try { await original.ConfigureAwait(false); return null; }
            catch (Exception source) { return PhysicalTaskFailureException.GetRaw(source); }
        }

        internal async Task VerifyRetainedStopFailure()
        {
            var stopFailure = await Failure(Stop());
            Ensure(stopFailure is not null, "Stop cannot claim release of retained module");
            var physical = ServerLifecycle!.DeferredServiceCleanupTaskForDiagnostics;
            Ensure(physical is not null, "actual ordered service owner exists");
            var failure = await Failure(physical!);
            // This exact source stage synthesizes one leaf when its original
            // fault already belongs to the strict supervisor. Never whitelist an
            // arbitrary aggregate/container containing unknown independent errors.
            Ensure(failure?.GetType() == typeof(InvalidOperationException) && failure.InnerException is null &&
                failure.Message == "Module retirement ended before release; dependent ownership remains retained.",
                "only the exact retained-module Stop sentinel is expected here");
            ExpectedFailures.Add(failure!);
            Ensure(IsExpectedTree(stopFailure!), "Stop outcome contains no unknown independent failure leaves");
        }

        private bool IsExpectedTree(Exception error)
        {
            if (ExpectedFailures.Any(expected => ReferenceEquals(error, expected))) return true;
            if (error is PhysicalTaskFailureException) return IsExpectedTree(PhysicalTaskFailureException.GetRaw(error));
            return error is AggregateException aggregate && aggregate.InnerExceptions.All(IsExpectedTree);
        }

        internal async Task Close(Exception? body)
        {
            List<Exception> failures = [];
            if (body is not null) failures.Add(body);
            Clock.CreateRelease.Set(); CallbackRelease.Set(); _replacement?.Release.TrySetResult();
            ReleaseCall(); ReleasePreparation(); ReleaseCleanupQuota();
            async Task Join(string phase, Task? original)
            {
                if (original is null) return;
                Exception? sourceFailure;
                try { sourceFailure = await Failure(original); }
                catch (Exception diagnostic)
                {
                    // Never pass a readiness/containment failure through the
                    // expected-source filter, even if source completed meanwhile.
                    failures.Add(new InvalidOperationException($"{phase}: diagnostic wait failed; original status={original.Status}; " +
                        $"module={Module?.State}; timers={Clock.Inner.ActiveTimerCount}", diagnostic));
                    return;
                }
                if (sourceFailure is not null && !IsExpectedTree(sourceFailure))
                    failures.Add(new InvalidOperationException($"{phase}: original source failed", sourceFailure));
            }
            foreach (var invocation in Invocations) await Join("public invocation", invocation);
            await Join("driver", Module?.RetirementControl?.Driver.Task);
            await Join("physical", Module?.RetirementOperation);
            await Join("observer", Module?.RetirementObservation);
            foreach (var waiter in Waiters) await Join("public waiter", waiter);
            if (_client is not null || _server is not null)
            {
                await Join("Stop invocation wrapper", Stop());
                await Join("original host Stop", _hostStop);
            }
            if (_server is not null)
            {
                var lifecycle = Read<SharpLinkServer.ServerLifecycleCoordinator>(_server, "_lifecycle");
                await Join("service physical", lifecycle.DeferredServiceCleanupTaskForDiagnostics);
                await Join("shutdown observer", lifecycle.ShutdownCleanupObserverTaskForDiagnostics);
            }
            if (AllowQuarantineFakeEnd)
            {
                Ensure(Quarantine && Module!.RetirementControl!.Driver.Task.IsCompletedSuccessfully &&
                    Module.RetirementControl.Driver.Task.Result.HasRetainedTimer &&
                    ReferenceEquals(Read<ITimer>(Module.RetirementControl.Driver, "_ownedTimer"), Clock.LastOwnedTimer),
                    "only verified quarantine may terminalize its known fake out of band");
                await Clock.EndKnownFakeOutOfBand();
            }
            await Join("provider timers", Clock.Inner.WaitForTimersDrainedAsync());
            var ended = Invocations.All(static task => task.IsCompleted) && (Module?.RetirementOperation?.IsCompleted ?? true) &&
                (Module?.RetirementControl?.Driver.Task.IsCompleted ?? true) && (Module?.RetirementObservation?.IsCompleted ?? true) &&
                (_stop?.IsCompleted ?? true) && (_hostStop?.IsCompleted ?? true) && Waiters.All(static task => task.IsCompleted) &&
                (ServerLifecycle?.DeferredServiceCleanupTaskForDiagnostics?.IsCompleted ?? true) &&
                (ServerLifecycle?.ShutdownCleanupObserverTaskForDiagnostics?.IsCompleted ?? true) &&
                Clock.Inner.ActiveTimerCount == 0 && Outcomes.All(static outcome => outcome.IsCompleted);
            OwnersEnded = ended;
            if (ended)
            {
                _cancellation.Dispose(); CallbackRelease.Dispose(); Clock.CreateRelease.Dispose();
                _replacement?.Dispose();
            }
            else { lock (Containment) Containment.Add(this); }
            if (!Quarantine && Module is not null && Module.State != SharpLinkDynamicModuleState.Released)
                failures.Add(new InvalidOperationException("ordinary control did not finish real module cleanup"));
            if (failures.Count != 0) throw new AggregateException(failures);
        }
    }

    private sealed class ReplacementAssembly : IDisposable
    {
        private readonly AssemblyLoadContext _context = new("retirement-control-" + Guid.NewGuid(), isCollectible: true);
        internal readonly TaskCompletionSource Entered = Signal();
        internal readonly TaskCompletionSource Release = Signal();
        internal Assembly Assembly { get; }
        internal ReplacementAssembly()
        {
            Assembly = _context.LoadFromAssemblyPath(typeof(RollbackMarker).Assembly.Location);
            var state = Assembly.GetType(typeof(RollbackState).FullName!, throwOnError: true)!;
            state.GetField(nameof(RollbackState.ManifestConstructionStarted))!.SetValue(null, Entered);
            state.GetField(nameof(RollbackState.ManifestConstructionRelease))!.SetValue(null, Release);
        }
        public void Dispose()
        {
            var state = Assembly.GetType(typeof(RollbackState).FullName!, throwOnError: true)!;
            state.GetField(nameof(RollbackState.ManifestConstructionStarted))!.SetValue(null, null);
            state.GetField(nameof(RollbackState.ManifestConstructionRelease))!.SetValue(null, null);
            _context.Unload();
        }
    }

    private sealed class ControlledProvider : TimeProvider
    {
        internal readonly ManualTimeProvider Inner = new();
        internal readonly TaskCompletionSource CreateEntered = Signal();
        internal readonly TaskCompletionSource Armed = Signal();
        internal readonly ManualResetEventSlim CreateRelease = new(false);
        internal bool BlockCreate;
        internal bool SynchronousHints;
        private TimerCallback? _callback;
        private object? _callbackState;
        internal void LateHint() => _callback?.Invoke(_callbackState);
        internal Exception? CreateFailure;
        internal Exception? DisposeFailure;
        internal OwnedTimer? LastOwnedTimer;
        internal int DisposeCalls;
        public override long TimestampFrequency => Inner.TimestampFrequency;
        public override long GetTimestamp() => Inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow();
        internal void Advance(TimeSpan elapsed) => Inner.Advance(elapsed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (state is not RetirementCancellationDriver) return Inner.CreateTimer(callback, state, dueTime, period);
            _callback = callback; _callbackState = state;
            CreateEntered.TrySetResult();
            if (SynchronousHints) callback(state);
            if (BlockCreate) CreateRelease.Wait();
            if (CreateFailure is { } error) global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return LastOwnedTimer = new OwnedTimer(this, Inner.CreateTimer(callback, state, dueTime, period));
        }
        internal ValueTask EndKnownFakeOutOfBand() => LastOwnedTimer?.EndKnownFakeOutOfBand() ?? ValueTask.CompletedTask;

        internal sealed class OwnedTimer(ControlledProvider owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var changed = inner.Change(dueTime, period);
                // At most one synchronous Change hint: continuous hostile hints
                // can starve a scheduler and are not a punctuality guarantee.
                if (owner.SynchronousHints) { owner.SynchronousHints = false; owner.LateHint(); }
                owner.Armed.TrySetResult();
                return changed;
            }
            public void Dispose() => inner.Dispose();
            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.DisposeCalls);
                return owner.DisposeFailure is { } error ? ValueTask.FromException(error) : inner.DisposeAsync();
            }
            internal ValueTask EndKnownFakeOutOfBand() => inner.DisposeAsync();
        }
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
    private static T Read<T>(object owner, string field) => (T)(owner.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!);
    private static int Count(Exception? error, Exception expected)
    {
        if (error is null) return 0;
        if (ReferenceEquals(error, expected)) return 1;
        if (error is AggregateException aggregate) return aggregate.InnerExceptions.Sum(item => Count(item, expected));
        return Count(error.InnerException, expected);
    }
    private static void Ensure(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
