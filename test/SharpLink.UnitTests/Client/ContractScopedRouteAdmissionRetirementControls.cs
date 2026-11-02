using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class ContractScopedRouteAdmissionRetirementControls
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DynamicGenerationRetiresBetweenCapacityAndAdmission(bool retirementThrows)
    {
        var fixture = new ContractScopedRouteHandoffFixture();
        var policy = new RecreatingAdmission(retirementThrows);
        Exception? primary = null;
        try
        {
            await fixture.InitializeDynamicAsync(policy);
            var candidate = fixture.View.Candidates[0];
            var oldState = (SharpLinkClient.DynamicEndpointState)fixture.View.Topology.Endpoints[0].Identity;
            await fixture.CompleteCallAsync(fixture.StartCall("R37 old generation warmup"), 0, fixture.Source, 3700);
            Require(policy.Has(candidate.Generation), "real warmup creates the old generation's lazy admission state");
            policy.Arm(candidate.Generation);
            var reservationCount = fixture.ReservationCount;
            var blocked = fixture.StartCall("R37 stale selected admission");
            await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(policy.CapacityAtEntry == 1 && fixture.Source.CallAdmissionReservationCount == 0 &&
                    fixture.Source.ActiveCallCount == 0 && !blocked.Task.IsCompleted,
                "the actual policy entry follows a positive scoped capacity check, before any exact reservation or returned Unary task");
            await fixture.RetireUnreservedGenerationAndReaddAsync();
            Require(oldState.FactoryReleased && policy.RetireCount(candidate.Generation) == 1 && !policy.Has(candidate.Generation),
                "actual physical and factory release retire the old lazy state while admission is still blocked");
            await fixture.ConfirmReplacementAsync();
            await fixture.CompleteCallAsync(fixture.StartCall("R37 replacement generation warmup"), 2, fixture.Replacement, 3701);
            var replacementGeneration = fixture.Replacement.EndpointGeneration;
            Require(replacementGeneration != candidate.Generation && policy.Has(replacementGeneration),
                "the replacement's independent real admission creates distinct current-generation state");
            var beforeReleaseReservations = fixture.ReservationCount;
            policy.Release.Set();
            await fixture.ExpectCallFailureAsync(blocked, error => retirementThrows
                ? error is AggregateException { InnerExceptions.Count: 2 } aggregate &&
                  aggregate.InnerExceptions[0] is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed } &&
                  ReferenceEquals(aggregate.InnerExceptions[1], policy.RetirementFailure)
                : error is SharpLinkException
                {
                    Code: SharpLinkErrorCode.Unavailable,
                    DetailCode: SharpLinkErrorDetails.Unavailable.NoBoundRoute
                });
            var staleReports = policy.StaleReports;
            Require(policy.Recreated && policy.RetireCount(candidate.Generation) == 2 && !policy.Has(candidate.Generation) &&
                    policy.Has(replacementGeneration),
                "failure-path lifecycle.Retire runs for the original released generation after TryAcquire recreates it, leaving replacement state untouched");
            Require(staleReports.Length == 1 && staleReports[0].Outcome.Endpoint.Generation == candidate.Generation &&
                    staleReports[0].Outcome.Endpoint.Endpoint.Id == candidate.Endpoint.Id &&
                    staleReports[0].Outcome.Kind == SharpLinkEndpointOutcomeKind.ConnectionClosed &&
                    !staleReports[0].Outcome.ResponseObserved && staleReports[0].Token == policy.StaleToken,
                "the granted stale lease receives exactly one original terminal outcome even when independent retirement throws");
            Require(fixture.ReservationCount == beforeReleaseReservations && beforeReleaseReservations == reservationCount + 1 &&
                    fixture.Source.CallAdmissionReservationCount == 0 && fixture.Source.ActiveCallCount == 0 &&
                    fixture.Replacement.CallAdmissionReservationCount == 0 && fixture.Replacement.ActiveCallCount == 0,
                "stale exact target never reserves any session, borrows a replacement token, or leaks physical ownership");
        }
        catch (Exception error) { primary = error; }
        policy.Release.Set(); // Open the test-owned policy gate before joining its original launcher.
        await fixture.CleanupGenerationAsync(primary);
    }

    private sealed class RecreatingAdmission(bool retirementThrows) : ISharpLinkEndpointAdmissionPolicy, ISharpLinkEndpointAdmissionLifecycle
    {
        private readonly object _gate = new();
        private readonly HashSet<long> _states = [];
        private readonly Dictionary<long, int> _retirements = [];
        private readonly List<(SharpLinkEndpointOutcome Outcome, long Token)> _reports = [];
        private long _nextToken;
        private long _armedGeneration = -1;
        private int _claimed;
        private bool _recreated;
        private long _staleToken;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        internal Exception RetirementFailure { get; } = new InvalidOperationException("R37 independent late generation retirement marker");
        internal int CapacityAtEntry { get; private set; }
        internal long StaleToken { get { lock (_gate) return _staleToken; } }
        internal bool Recreated { get { lock (_gate) return _recreated; } }
        internal bool Has(long generation) { lock (_gate) return _states.Contains(generation); }
        internal int RetireCount(long generation) { lock (_gate) return _retirements.GetValueOrDefault(generation); }
        internal (SharpLinkEndpointOutcome Outcome, long Token)[] StaleReports
        {
            get { lock (_gate) return _reports.Where(report => report.Token == _staleToken).ToArray(); }
        }
        internal void Arm(long generation) => Volatile.Write(ref _armedGeneration, generation);

        public SharpLinkEndpointAdmissionDecision TryAcquire(in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
        {
            var stale = endpoint.Generation == Volatile.Read(ref _armedGeneration) && Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
            if (stale)
            {
                CapacityAtEntry = endpoint.ReadyConnectionCount;
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The test-owned admission barrier was not released.");
            }
            lock (_gate)
            {
                var token = ++_nextToken;
                var added = _states.Add(endpoint.Generation);
                if (stale)
                {
                    Require(added && _retirements.GetValueOrDefault(endpoint.Generation) == 1,
                        "stale TryAcquire must really recreate state after the real early retirement");
                    _recreated = true;
                    _staleToken = token;
                }
                return new(true, token, null);
            }
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            lock (_gate) _reports.Add((outcome, token));
        }

        public void Retire(in SharpLinkEndpointCandidate endpoint)
        {
            bool fail;
            lock (_gate)
            {
                var count = _retirements.GetValueOrDefault(endpoint.Generation) + 1;
                _retirements[endpoint.Generation] = count;
                _states.Remove(endpoint.Generation);
                fail = retirementThrows && endpoint.Generation == _armedGeneration && count == 2 && _recreated;
            }
            if (fail) throw RetirementFailure;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

// Reuses only the frozen real protocol/owner fixture; no production hooks or APIs added.
internal sealed partial class ContractScopedRouteHandoffFixture
{
    internal async Task RetireUnreservedGenerationAndReaddAsync()
    {
        _initialized = false;
        // Prepare and an ordinary call may finish before the original successful Bind's
        // result-owner finally returns its cold control permit. The removal cleanup kind
        // is deterministic only after that exact physical control tail has also released.
        // This counter is NOT treated as Task completion: both original Bind tasks stay
        // in the fixture ledger and are independently joined during cleanup.
        await AwaitAsync(() => Source.Binding.ActiveOperationCount == 0,
            "original source Bind result-owner tail must release before unreserved topology retirement");
        Ensure(Source.ActiveCallCount == 0 && Source.CallAdmissionReservationCount == 0,
            "selected-but-not-yet-admitted old generation has no physical call owner");
        await ApplyGenerationSnapshotAsync(new(2, [GenerationEndpoint("route-b", "before")]));
        await _peers[0].DisposeEntered.Task.WaitAsync(Bound);
        await _generationFactories[0].DisposeEntered.Task.WaitAsync(Bound);
        OwnedOperation? cleanup = null;
        OwnedOperation? release = null;
        await AwaitAsync(() =>
        {
            CaptureFrameworkOwners("DynamicClusterTopologyRetirementCleanup", "original R37 retired physical cleanup");
            CaptureFrameworkOwners("DynamicClusterRetiredTopologyRelease", "original R37 retired factory release");
            cleanup = SnapshotOperations().SingleOrDefault(operation => operation.Stage == "original R37 retired physical cleanup");
            release = SnapshotOperations().SingleOrDefault(operation => operation.Stage == "original R37 retired factory release");
            return cleanup is not null && release is not null;
        }, "original source cleanup and generation release tasks must be captured behind their real disposal gates");
        CapturePeerOwners();
        CaptureGenerationTasks();
        _peers[0].ReleaseDispose.TrySetResult();
        await ObserveAsync(cleanup!);
        _generationFactories[0].ReleaseDispose.TrySetResult();
        await ObserveAsync(release!);
        Ensure(Source.ActiveCallCount == 0 && Source.CallAdmissionReservationCount == 0 && !Source.Session.IsConnected,
            "old physical generation really exited before releasing stale admission");
        await ApplyGenerationSnapshotAsync(new(3,
            [GenerationEndpoint("route-a", "after"), GenerationEndpoint("route-b", "before")]));
        await _generationFactories[2].ConnectEntered.Task.WaitAsync(Bound);
        OwnedOperation? reconnect = null;
        await AwaitAsync(() =>
        {
            CaptureFrameworkOwners("DynamicClusterReconnect", "original R37 replacement reconnect");
            reconnect = SnapshotOperations().SingleOrDefault(operation => operation.Stage == "original R37 replacement reconnect" && !operation.Task.IsCompleted);
            return reconnect is not null;
        }, "replacement generation's original reconnect Task is captured before releasing its factory entry");
        _generationFactories[2].ReleaseConnect.TrySetResult();
        await ObserveAsync(reconnect!);
        await AwaitAsync(() => View.Topology.Endpoints.Length == 2 && View.Topology.Endpoints[0].Endpoint.Id == "route-a",
            "replacement generation topology did not publish");
        Replacement = View.Topology.Endpoints[0].Connections.Single();
        await ReadBindAsync(2);
        await AwaitAsync(() =>
        {
            CaptureFrameworkOwners("ContractBinding", "original BindContractDemand");
            return SnapshotOperations().Count(operation => operation.Stage == "original BindContractDemand") == 3;
        }, "replacement's original Bind Task must be captured while its own response stays held");
        CaptureGenerationTasks();
        Ensure(GenerationFactoryCount == 3 && !View.HasEligibleTarget() && !_replyStarted[2],
            "new generation starts with its own real Ready session, never a borrowed old token");
        _initialized = true;
    }
}
