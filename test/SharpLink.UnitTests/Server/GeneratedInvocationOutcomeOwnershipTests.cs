using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

// Real started Server/permit/call-state and actual Runtime pump/Server cleanup components.
// The transport is a component pipe, not TCP or generated package acceptance. The diagnostic
// call-state use prevents fixture-reference ABA; it deliberately excludes pool-return proof.
public sealed class GeneratedInvocationOutcomeOwnershipTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false, 0)]
    [Arguments(false, 1)]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 0)]
    [Arguments(true, 1)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task SelectedTerminalOwnsOriginalSourceInBothOrders(bool sourceReady, int outcome)
    {
        await Run(async f =>
        {
            var source = f.NewSource(sourceReady, outcome);
            f.StartPump(source);
            await source.Entered.Task.WaitAsync(Bound);
            if (!sourceReady)
            {
                Ensure(f.State.TryCancel(ServerCallCancellationReason.RemoteCancel), "real terminal selected");
                await f.ExpectPumpSuccess();
                Ensure(!source.Operation!.IsCompleted && f.Permit.HasPhysicalTails && source.Disposals == 0,
                    "logical pump exit retains actual pending source and forbids concurrent Dispose");
                f.BeginCleanup();
                f.DisposeState();
                var unexpectedlyAcquired = f.Captured.TryAcquire();
                if (unexpectedlyAcquired) f.Captured.ReleaseUse();
                Ensure(!f.Cleanup!.IsCompleted && f.Connection.ActiveCalls == 1 &&
                    !unexpectedlyAcquired, "physical owner held after actual disposal request closes new use");
                source.Complete();
            }
            else await f.ExpectPumpSuccess();
            f.BeginCleanup();
            await f.AssertFinished(null);
            Ensure(source.Disposals == 1 && source.Operation!.IsCompleted,
                "original operation exits before exactly one physical Dispose");
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ActualCooperativeIteratorCancellationKeepsInvocationOwnership(bool sourceReady)
    {
        await Run(async f =>
        {
            var source = f.NewSource(sourceReady, 3, cooperative: true);
            f.StartPump(source);
            await source.Entered.Task.WaitAsync(Bound);
            if (!sourceReady)
            {
                Ensure(f.State.TryCancel(ServerCallCancellationReason.RemoteCancel), "real cancellation selected");
                await source.CancellationObserved.Task.WaitAsync(Bound);
                await f.ExpectPumpSuccess();
                Ensure(!source.Operation!.IsCompleted && source.Disposals == 0,
                    "actual canceled Delay's iterator remains held before its original MoveNext exits");
                f.BeginCleanup();
                source.AllowCooperativeExit.TrySetResult();
            }
            else await f.ExpectPumpSuccess();
            f.BeginCleanup();
            await f.AssertFinished(null);
            Ensure(source.CancellationObserved.Task.IsCompleted && source.Operation!.IsCanceled && source.Disposals == 1,
                "actual supplied-token Delay canceled, yet accepted invocation result is not cleanup fault");
        });
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SameRawSourceAndDisposeObjectKeepsIndependentDisposeStage(bool sourceReady, bool rawCancellation)
    {
        await Run(async f =>
        {
            Exception raw = rawCancellation ? new OperationCanceledException("same raw source/dispose")
                : new InvalidOperationException("same raw source/dispose");
            var source = f.NewSource(sourceReady, rawCancellation ? 2 : 1, raw, raw);
            f.StartPump(source);
            await source.Entered.Task.WaitAsync(Bound);
            if (!sourceReady)
            {
                Ensure(f.State.TryCancel(ServerCallCancellationReason.RemoteCancel), "terminal selected");
                await f.ExpectPumpSuccess();
                f.BeginCleanup();
                source.Complete();
            }
            else await f.ExpectPumpSuccess();
            f.BeginCleanup();
            await f.AssertFinished(raw);
            Ensure(source.Disposals == 1, "independent Dispose occurrence executed once");
        });
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task RealIssuerRejectsMissingCompletedWrongOrClosedIdentity(int refusal)
    {
        await Run(async f =>
        {
            var request = Fixture.RequestId;
            if (refusal == 1)
                Ensure(f.State.TryClaimResponse(), "actual Completed terminal selected");
            else if (refusal != 0)
                Ensure(f.State.TryCancel(ServerCallCancellationReason.RemoteCancel), "real strong terminal selected");
            if (refusal == 2) request++;
            if (refusal == 3)
            {
                // Same live state/request and actual terminal, differing only in generation.
                // This trusted-internal component constructs an invalid version; no pool reuse claim.
                var current = f.Captured;
                var liveAcquired = current.TryAcquire();
                if (liveAcquired) current.ReleaseUse();
                Ensure(liveAcquired, "original live generation remains acquirable");
                var stale = new ServerCallCancellationLease(f.State, Fixture.RequestId,
                    unchecked(current.Generation + 1));
                var unexpectedlyAcquired = stale.TryAcquire();
                if (unexpectedlyAcquired) stale.ReleaseUse();
                Ensure(!unexpectedlyAcquired, "only wrong generation rejects the same live state/request");
                f.Permit.RememberPhysicalCallState(in stale);
            }
            if (refusal == 4) f.DisposeState();
            var raw = new OperationCanceledException("receipt refusal preserves exact source raw");
            var source = f.NewSource(false, 2, raw);
            source.Complete();
            GeneratedInvocationOutcomeReceipt? receipt = null;
            f.Permit.TryAcceptInvocationOutcome(source.Pending.Task, request, ref receipt);
            Ensure(receipt is null, "real issuer must not fabricate a terminal/generation receipt");
            f.StartFinalizer(source, request);
            await f.ExpectPumpSuccess();
            f.BeginCleanup();
            await f.AssertFinished(raw);
            Ensure(source.Disposals == 1, "rejection still physically disposes original enumerator");
        });
    }

    [Test]
    public async Task AcceptedHeldSourceAndDisposalKeepSameRealStopPhysicallyPending()
    {
        await Run(async f =>
        {
            f.Service.Hold = true;
            var source = f.NewSource(false, 1, holdDispose: true);
            f.StartPump(source);
            await source.Entered.Task.WaitAsync(Bound);
            Ensure(f.State.TryCancel(ServerCallCancellationReason.RemoteCancel), "real terminal selected");
            await f.ExpectPumpSuccess();
            f.BeginCleanup();
            var stop = f.Stop();
            await Until(() => f.Server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics.Drain is not null);
            var drain = f.Server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics.Drain!;
            Ensure(!stop.IsCompleted && !drain.IsCompleted && !source.Operation!.IsCompleted &&
                !f.Cleanup!.IsCompleted && f.Connection.ActiveCalls == 1 && source.Disposals == 0,
                "same real Stop has started its physical drain but original source/permit still own work");
            source.Complete();
            await source.DisposeEntered.Task.WaitAsync(Bound);
            Ensure(!stop.IsCompleted && !drain.IsCompleted && f.Service.Disposals == 0,
                "source exit alone cannot discharge held actual enumerator Dispose");
            source.AllowDispose.TrySetResult();
            await f.Service.Entered.Task.WaitAsync(Bound);
            Ensure(!stop.IsCompleted && !drain.IsCompleted && f.Scope.Disposals == 0 && f.Connection.ActiveCalls == 1,
                "service Dispose is a distinct physical obligation before scope/capacity release");
            f.Service.Allow.TrySetResult();
            await f.AssertFinished(null);
            Ensure(ReferenceEquals(stop, f.Stop()) && drain.IsCompletedSuccessfully &&
                source.Disposals == 1 && f.Service.Disposals == 1 && f.Scope.Disposals == 1,
                "same original Stop/drain and every original physical barrier actually exit");
        });
    }

    private static async Task Run(Func<Fixture, Task> body)
    {
        var fixture = new Fixture();
        Exception? failure = null;
        try { await fixture.Initialize(); await body(fixture); }
        catch (Exception error) { failure = error; }
        try { await fixture.Close(); }
        catch (Exception error) { failure = Combine(failure, error); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Fixture
    {
        internal const long RequestId = 71;
        private static readonly ConcurrentBag<Fixture> Quarantine = [];
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly Listener _listener = new();
        private readonly Dictionary<Task, Observation> _observations = new(ReferenceEqualityComparer.Instance);
        private readonly List<Source> _sources = [];
        private readonly StripedLongMap<ServerCallCancellationState> _calls = new();
        private bool _stateDisposed, _pinHeld, _cleanupStarted, _expectedVerified;
        private Exception? _verifiedFailure;
        private Task? _start, _pump, _stop, _connectionClose, _sessionDispose, _serverDispose;
        private Task? _inputComplete, _outputComplete;
        private CancellationTokenSource? _finalizerLifetime;
        internal SharpLinkServer Server { get; private set; } = null!;
        internal RpcSession Session { get; private set; } = null!;
        internal ServerConnectionState Connection { get; private set; } = null!;
        internal ServerRequestPermit Permit { get; private set; } = null!;
        internal ServerCallCancellationState State { get; private set; } = null!;
        internal ServerCallCancellationLease Captured { get; private set; }
        internal Task? Cleanup { get; private set; }
        internal HeldService Service { get; } = new();
        internal Scope Scope { get; } = new();

        internal async Task Initialize()
        {
            Server = (SharpLinkServer)SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableAutomaticServiceRegistration().UseTransport(_listener).Build();
            _start = Server.StartAsync().AsTask();
            var startFailure = await Outcome(_start);
            if (startFailure is not null) ExceptionDispatchInfo.Capture(startFailure).Throw();
            await _listener.Entered.Task.WaitAsync(Bound);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport("generated-invocation-owner",
                _input.Reader, _output.Writer, RpcSessionTestFixture.ServerOptions());
            var bridge = new ServerGeneratedBridge(Server, Session, _calls);
            Connection = new ServerConnectionState(Session, bridge, _calls, CancellationToken.None, TimeProvider.System);
            Ensure(Connection.MarkReady(null), "actual component connection ready");
            Ensure(Server.TryReserveCall(Connection, mayDecode: false, out var permit) ==
                ServerCallAdmissionResult.Acquired && permit is not null, "real running Server owns original capacity");
            Permit = permit!;
            State = ServerCallCancellationState.Rent(RequestId, default, TimeProvider.System,
                Connection.ConnectionToken, CancellationToken.None, supportsCooperativeCancellation: true);
            _calls.Set(RequestId, State);
            Captured = State.CaptureLease(RequestId);
            _pinHeld = Captured.TryAcquire();
            Ensure(_pinHeld && State.TryActivateRequest(Permit), "actual exact state and permit activate");
            var captured = Captured;
            Permit.RememberPhysicalCallState(in captured);
        }

        internal Source NewSource(bool ready, int outcome, Exception? raw = null,
            Exception? disposeFailure = null, bool cooperative = false, bool holdDispose = false)
        {
            var source = new Source(this, ready, outcome, raw, disposeFailure, cooperative, holdDispose);
            _sources.Add(source);
            return source;
        }

        internal void StartPump(Source source)
        {
            var bridge = new ServerGeneratedBridge(Server, Session, _calls, Permit);
            _pump = bridge.PumpOutboundStreamAsync(RequestId, 1, source, new UnusedCodec(), true,
                new RpcMethodFacts(101, 202, RpcMethodKind.ServerStreaming, 0, true, true), State.InvocationToken).AsTask();
            source.HandedOff = true;
        }

        internal void StartFinalizer(Source source, long expectedRequest)
        {
            var method = typeof(RpcSession).GetMethod("FinishGeneratedEnumeratorAsync",
                BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(int));
            _finalizerLifetime = new CancellationTokenSource();
            _pump = ((ValueTask)method.Invoke(null, [source, source.Pending.Task, _finalizerLifetime,
                Permit, true, null, GeneratedPendingOperationRole.InvocationMoveNext, expectedRequest])!).AsTask();
            source.HandedOff = true;
        }

        internal async Task ExpectPumpSuccess()
        {
            Ensure(_pump is not null, "one actual pump/finalizer exists");
            var failure = await Outcome(_pump!);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        internal void BeginCleanup()
        {
            if (_cleanupStarted) return;
            Ensure(_pump is null || _pump.IsCompleted, "invocation is joined before service-cleanup election");
            _cleanupStarted = true;
            var lease = new ServiceLease(Service, Scope, disposeService: true);
            if (Permit.BeginPhysicalServiceCleanup())
            {
                var outgoing = Permit.JoinPhysicalTailsAsync().AsTask();
                Cleanup = (Task)typeof(SharpLinkServer).GetMethod("CompletePhysicalServiceCleanupAsync",
                    BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                    [Permit, lease, outgoing, ValueTask.CompletedTask])!;
                Permit.AttachPhysicalServiceCleanup(Cleanup);
                FrameworkTaskRegistration? receipt = null;
                Server.TrackStrictPhysicalRuntimeTask(Cleanup, "GeneratedInvocationControlCleanup", ref receipt);
                Ensure(receipt is { IsStrictPhysical: true } && ReferenceEquals(receipt.Task, Cleanup),
                    "actual cleanup accepted by exact strict supervisor receipt");
            }
            else Cleanup = lease.DisposeAsync().AsTask();
            Permit.Dispose();
            DisposeState();
        }

        internal void DisposeState()
        {
            if (_stateDisposed || State is null) return;
            _stateDisposed = true;
            _calls.TryRemove(RequestId, State);
            State.Dispose(); // diagnostic pin keeps the fixture reference safe, not new-acquisition eligibility.
        }

        internal Task Stop() => _stop ??= Server.StopAsync(TimeSpan.Zero).AsTask();

        internal async Task AssertFinished(Exception? expected)
        {
            Ensure(Cleanup is not null, "actual cleanup exists");
            var physical = await Outcome(Cleanup!);
            if (expected is null) RequireSuccess(physical, "accepted source has no independent physical failure");
            else if (physical is null || !ReferenceEquals(PhysicalTaskFailureException.GetRaw(physical), expected))
                throw Combine(new InvalidOperationException("Expected exact independent cleanup raw identity, not source+Dispose duplicate."),
                    physical ?? new InvalidOperationException("Expected cleanup failure was absent."));
            Ensure(Connection.ActiveCalls == 0 && Server.ActiveCallCountForDiagnostics == 0 &&
                Service.Disposals == 1 && Scope.Disposals == 1, "actual physical cleanup refunds original owner once");
            var stopped = await Outcome(Stop());
            if (expected is null) RequireSuccess(stopped, "normal accepted source does not poison actual Stop");
            else
            {
                RequireOnly(stopped, expected);
                await Until(() => Server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics.Observed);
                var drain = Server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics;
                Ensure(drain.Drain is { IsCompleted: true } && ReferenceEquals(drain.Failure, expected),
                    "completed actual strict supervisor owns the independent exact raw failure");
                RequireOnly(Server.LifecycleForDiagnostics.StrictPhysicalTerminalFailureForDiagnostics, expected);
            }
            _verifiedFailure = expected;
            _expectedVerified = true;
        }

        private sealed class Observation(Task source)
        {
            internal readonly Task<Exception?> Result = Observe(source);
            internal bool Consumed;
            internal bool CompletionOnly;
            private static async Task<Exception?> Observe(Task task)
            {
                try { await task.ConfigureAwait(false); return null; }
                catch (Exception error) { return error; }
            }
        }

        private Observation Track(Task task)
        {
            if (!_observations.TryGetValue(task, out var observed))
                _observations.Add(task, observed = new Observation(task));
            return observed;
        }

        private async Task<Exception?> Outcome(Task task)
        {
            var observed = Track(task);
            var result = await observed.Result.WaitAsync(Bound);
            observed.Consumed = true;
            return result;
        }

        internal async Task Close()
        {
            Exception? failures = null;
            foreach (var source in _sources) source.ReleaseAll();
            Service.Allow.TrySetResult();
            async Task Join(Task? task, bool lifecycleReplay = false)
            {
                if (task is null) return;
                var observed = Track(task);
                if (observed.Consumed) return;
                try
                {
                    var error = await Outcome(task);
                    if (error is null || observed.CompletionOnly) return;
                    if (lifecycleReplay && _expectedVerified && _verifiedFailure is not null)
                        RequireOnly(error, _verifiedFailure);
                    else failures = Combine(failures, error);
                }
                catch (Exception error) { failures = Combine(failures, error); }
            }
            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception error) { failures = Combine(failures, error); }
            }
            bool ObservedExit(Task? task) => task is null ||
                _observations.TryGetValue(task, out var seen) && seen.Consumed && seen.Result.IsCompleted;
            Task[] CaptureSources()
            {
                var tasks = new List<Task>();
                foreach (var source in _sources)
                {
                    // If no actual pump/finalizer accepted ownership, the fixture owns raw
                    // source outcome. Otherwise this observer joins without copying business/permit errors.
                    foreach (var task in source.Operation is { } operation
                                 ? new[] { source.Pending.Task, operation } : new Task[] { source.Pending.Task })
                    {
                        Track(task).CompletionOnly = source.HandedOff;
                        tasks.Add(task);
                    }
                }
                return tasks.Distinct<Task>(ReferenceEqualityComparer.Instance).ToArray();
            }
            void QuarantineBeforeTeardown(string boundary)
            {
                Quarantine.Add(this);
                failures = Combine(failures, new InvalidOperationException(
                    "Fixture quarantined before resource teardown: " + boundary));
                ExceptionDispatchInfo.Capture(failures).Throw();
            }
            // Logical cancellation is a signal, not proof of physical exit. In particular,
            // a cooperative iterator whose test body failed before selecting a terminal still exits.
            if (State is not null && !_stateDisposed)
                Attempt(() => State.TryCancel(ServerCallCancellationReason.RemoteCancel));
            await Join(_start);
            await Join(_pump);
            var sources = CaptureSources();
            foreach (var source in sources) await Join(source);
            await Join(_start);
            await Join(_pump);
            foreach (var source in CaptureSources()) await Join(source);
            if (!ObservedExit(_start) || !ObservedExit(_pump) || CaptureSources().Any(task => !ObservedExit(task)))
            {
                QuarantineBeforeTeardown("actual startup/invocation/source is still owned");
                return;
            }
            if (Permit is not null) Attempt(BeginCleanup);
            await Join(Cleanup);
            await Join(Cleanup); // Rejoin an unconsumed outcome even if it completed after the first timeout.
            if (Permit is not null && (Cleanup is null || !ObservedExit(Cleanup)))
            {
                QuarantineBeforeTeardown("actual service cleanup is still owned");
                return;
            }
            if (Permit is not null) Attempt(Permit.Dispose);
            if (Connection is not null && Connection.ActiveCalls != 0 ||
                Server is not null && Server.ActiveCallCountForDiagnostics != 0)
            {
                QuarantineBeforeTeardown("actual permit/capacity has not been refunded");
                return;
            }
            Attempt(DisposeState);
            if (Server is not null) { Attempt(() => _stop ??= Server.StopAsync(TimeSpan.Zero).AsTask()); await Join(_stop, true); }
            if (Connection is not null)
            {
                Attempt(() => _connectionClose ??= Connection.CloseAsync().AsTask());
                await Join(_connectionClose);
                await Join(Connection.ServiceCleanupTask);
            }
            if (Session is not null)
            {
                Attempt(() => _sessionDispose ??= Session.DisposeAsync().AsTask());
                await Join(_sessionDispose);
            }
            if (Server is not null)
            {
                Attempt(() => _serverDispose ??= Server.DisposeAsync().AsTask());
                await Join(_serverDispose, true);
                await Join(Server.LifecycleForDiagnostics.TerminalFailureObserverTaskForDiagnostics);
                await Join(Server.LifecycleForDiagnostics.ShutdownCleanupObserverTaskForDiagnostics);
                await Join(Server.LifecycleForDiagnostics.DeferredServiceCleanupTaskForDiagnostics, true);
                await Join(Server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics.Drain, true);
            }
            // Rejoin every unconsumed observer even if its source completed AFTER a prior timeout.
            foreach (var entry in _observations.ToArray())
                await Join(entry.Key, ReferenceEquals(entry.Key, _stop) || ReferenceEquals(entry.Key, _serverDispose));
            var exited = _observations.Values.All(x => x.Consumed && x.Result.IsCompleted) &&
                (Connection is null || Connection.ActiveCalls == 0) &&
                (Server is null || Server.FrameworkTaskSnapshotForDiagnostics.ActiveTasks == 0 &&
                    Server.ActiveCallCountForDiagnostics == 0);
            if (!exited)
            {
                Quarantine.Add(this);
                failures = Combine(failures, new InvalidOperationException("Fixture quarantined: actual owners/outcomes remain pending."));
            }
            else
            {
                if (_pinHeld) { _pinHeld = false; Attempt(Captured.ReleaseUse); }
                // Reflection setup may fail before handing this CTS to Runtime; keep the
                // exact instance until every actual operation has exited, then dispose independently.
                if (_finalizerLifetime is not null) Attempt(_finalizerLifetime.Dispose);
                Attempt(() => _inputComplete ??= _input.Writer.CompleteAsync().AsTask());
                await Join(_inputComplete);
                Attempt(() => _outputComplete ??= _output.Reader.CompleteAsync().AsTask());
                await Join(_outputComplete);
                foreach (var entry in _observations.ToArray()) await Join(entry.Key);
                if (_observations.Values.Any(x => !x.Consumed || !x.Result.IsCompleted))
                {
                    Quarantine.Add(this);
                    failures = Combine(failures, new InvalidOperationException("Fixture pipe cleanup remains physically owned."));
                }
            }
            if (failures is not null) ExceptionDispatchInfo.Capture(failures).Throw();
        }
    }

    private sealed class Source : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private readonly Fixture _fixture;
        private readonly bool _ready, _cooperative;
        private readonly int _outcome;
        private readonly Exception _raw;
        private readonly Exception? _disposeFailure;
        private IAsyncEnumerator<int>? _inner;
        internal readonly TaskCompletionSource<bool> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Entered = Signal(), CancellationObserved = Signal();
        internal readonly TaskCompletionSource AllowCooperativeExit = Signal(), DisposeEntered = Signal(), AllowDispose = Signal();
        internal Task? Operation;
        internal bool HandedOff;
        internal int Disposals;
        internal Source(Fixture fixture, bool ready, int outcome, Exception? raw,
            Exception? disposeFailure, bool cooperative, bool holdDispose)
        {
            _fixture = fixture; _ready = ready; _outcome = outcome; _cooperative = cooperative;
            _raw = raw ?? (outcome == 2 ? new OperationCanceledException("source raw") : new InvalidOperationException("source raw"));
            _disposeFailure = disposeFailure;
            if (!holdDispose) AllowDispose.TrySetResult();
            if (ready) AllowCooperativeExit.TrySetResult();
        }
        public int Current => throw new InvalidOperationException("No item should be serialized in these controls.");
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (_cooperative) _inner = Cooperative(cancellationToken).GetAsyncEnumerator(cancellationToken);
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            Ensure(Operation is null, "one original MoveNext only");
            Entered.TrySetResult();
            if (_ready) Ensure(_fixture.State.TryCancel(ServerCallCancellationReason.RemoteCancel), "source-ready terminal selected after user entry");
            if (_cooperative)
            {
                var task = _inner!.MoveNextAsync().AsTask();
                Operation = task;
                if (_ready) Ensure(task.IsCompleted, "cooperative source-ready precondition is actual completion");
                return new ValueTask<bool>(task);
            }
            if (_ready) Complete();
            Operation = Pending.Task;
            return new ValueTask<bool>(Pending.Task);
        }
        private async IAsyncEnumerable<int> Cooperative([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await AllowCooperativeExit.Task;
                throw;
            }
            yield break;
        }
        internal void Complete()
        {
            if (_outcome == 0) Pending.TrySetResult(false);
            else if (_outcome == 3) Pending.TrySetCanceled(new CancellationToken(true));
            else Pending.TrySetException(_raw);
        }
        internal void ReleaseAll()
        {
            if (Operation is null || _cooperative) Pending.TrySetResult(false); // Unused placeholder, never an invented source fault.
            else Complete();
            AllowCooperativeExit.TrySetResult();
            AllowDispose.TrySetResult();
        }
        public async ValueTask DisposeAsync()
        {
            Ensure(Operation is null || Operation.IsCompleted, "actual MoveNext must exit before Dispose starts");
            Ensure(Interlocked.Increment(ref Disposals) == 1, "Dispose executed once");
            DisposeEntered.TrySetResult();
            await AllowDispose.Task;
            if (_inner is not null) await _inner.DisposeAsync();
            if (_disposeFailure is not null) ExceptionDispatchInfo.Capture(_disposeFailure).Throw();
        }
    }

    private sealed class HeldService : IAsyncDisposable
    {
        internal readonly TaskCompletionSource Entered = Signal(), Allow = Signal();
        internal bool Hold;
        internal int Disposals;
        public async ValueTask DisposeAsync()
        {
            Ensure(Interlocked.Increment(ref Disposals) == 1, "actual service disposed once");
            Entered.TrySetResult();
            if (Hold) await Allow.Task;
        }
    }
    private sealed class Scope : IServiceScope
    {
        internal int Disposals;
        public IServiceProvider ServiceProvider => throw new InvalidOperationException("No service resolution in cleanup component.");
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    private sealed class Listener : IServerTransportListener
    {
        internal readonly TaskCompletionSource Entered = Signal();
        public EndPoint? LocalEndPoint => null;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation-only listener cannot accept a connection.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class UnusedCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer) => throw new InvalidOperationException("No item expected.");
        public int Deserialize(in ReadOnlySequence<byte> buffer) => throw new InvalidOperationException("No decode expected.");
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Exception Combine(Exception? first, Exception next) => first is null ? next : new AggregateException(first, next);
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void RequireSuccess(Exception? failure, string message)
    {
        if (failure is not null) throw new AggregateException(new InvalidOperationException(message), failure);
    }
    private static void RequireOnly(Exception? failure, Exception expected)
    {
        Ensure(failure is not null, "expected strict failure is present");
        failure = PhysicalTaskFailureException.GetRaw(failure!);
        if (failure is AggregateException aggregate)
        {
            Ensure(aggregate.InnerExceptions.Count != 0, "nonempty failure aggregate");
            foreach (var inner in aggregate.InnerExceptions) RequireOnly(inner, expected);
        }
        else if (!ReferenceEquals(failure, expected))
            throw new AggregateException(new InvalidOperationException("Every retained leaf must be the exact expected independent stage."), failure);
    }
    private static async Task Until(Func<bool> predicate)
    {
        var started = Stopwatch.GetTimestamp();
        while (!predicate())
        {
            if (Stopwatch.GetElapsedTime(started) >= Bound) throw new TimeoutException("Actual lifecycle boundary did not publish.");
            await Task.Yield();
        }
    }
}
