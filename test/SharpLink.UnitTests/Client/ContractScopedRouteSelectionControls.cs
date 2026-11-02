using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

// Source-only R03/R04/R07/R08/R10/R11/R12 controls. Runtime/discovery is UNRUN.
public sealed class ContractScopedRouteSelectionControls
{
    [Test]
    public Task RoundRobinTwoEligibleOneUnbound() => RunAsync(async fixture =>
    {
        await fixture.InitializeAsync(SharpLinkLoadBalancingStrategy.RoundRobin);
        var selection = fixture.View.Topology.Selection!;
        var map = fixture.View.Topology.IndexMap;
        for (var call = 0; call < 6; call++)
        {
            var before = ContractScopedRouteSelectionFixture.Read<int>(selection, "_roundRobinCursor");
            var start = unchecked((uint)(before + 1));
            var expected = (int)(start % 3);
            if (expected == 1) expected = 2;
            var actual = await fixture.CallAsync(300 + call);
            Ensure(actual == expected &&
                   ContractScopedRouteSelectionFixture.Read<int>(selection, "_roundRobinCursor") == unchecked(before + 1),
                "actual RR switch must advance its existing cursor once and skip middle index 1 without compaction");
            Ensure(ReferenceEquals(map, fixture.View.Topology.IndexMap), "ordinary calls cannot replace the index map");
        }
        Ensure(fixture.ReservationCount == 6, "each ordinary call owns exactly one actual physical reservation");
    });

    [Test]
    public Task RandomTwoEligibleOneUnbound() => RunAsync(async fixture =>
    {
        await fixture.InitializeAsync(SharpLinkLoadBalancingStrategy.Random);
        // Exhaustive deterministic oracle for the exact existing kernel domain, not a
        // distribution assertion over Random.Shared and not a flaky must-hit-both test.
        var indices = Enumerable.Range(0, 2)
            .Select(offset => EndpointSelectionKernel.SelectRandomIndex(3, 0b010, 2, offset)).ToArray();
        Ensure(indices.SequenceEqual(new[] { 0, 2 }), "both random offsets retain the original sparse endpoint indices");
        for (var call = 0; call < 4; call++)
        {
            Ensure(fixture.View.Buckets.Count(bucket => bucket.EligibleCount() != 0) == 2,
                "the actual Random switch must be reached, never the one-eligible fast path");
            var actual = await fixture.CallAsync(400 + call);
            Ensure(indices.Contains(actual), "actual Random selection and exact reservation must stay inside the enumerated domain");
        }
        Ensure(fixture.ReservationCount == 4, "all actual Random selections acquired and released one physical reservation");
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task LiveCapacityZeroBeforeSelector(bool allZero) => RunAsync(async fixture =>
    {
        var admission = new AdmissionProbe();
        await fixture.InitializeAsync(SharpLinkLoadBalancingStrategy.PowerOfTwoChoices, admission);
        var view = fixture.View;
        fixture.Owner(0).ObserveFatalFailureForAdmission();
        if (allZero) fixture.Owner(2).ObserveFatalFailureForAdmission();
        Ensure(ReferenceEquals(view, fixture.View) && view.Buckets[0].EligibleCount() == 0,
            "the already-captured route view must observe the exact owner cut through live capacity");
        if (allZero)
        {
            await fixture.ExpectCallFailureAsync(IsNoBoundRoute);
            Ensure(admission.Acquisitions.Length == 0 && admission.Reports.Length == 0,
                "all-zero must fail exact NoBoundRoute before admission, without divide-by-zero or fake outcome");
        }
        else
        {
            Ensure(await fixture.CallAsync(700) == 2, "the remaining eligible endpoint must be selected");
            Ensure(admission.Acquisitions.Select(item => item.Endpoint.Id).SequenceEqual(new[] { "route-c" }),
                "a bucket cut before capacity sampling must never be offered to admission");
        }
    });

    [Test]
    public Task LiveCapacityZeroInsideCustomCallback() => RunAsync(async fixture =>
    {
        var admission = new AdmissionProbe();
        var calls = 0;
        var selector = new SelectorProbe((in SharpLinkEndpointSelectionContext context) =>
        {
            calls++;
            Ensure(context.Count == 3, "custom selector sees full endpoint indices");
            if (calls == 1)
            {
                Ensure(context.ExcludedMask == 0b010 && context[0].ReadyConnectionCount == 1,
                    "the selected owner must be eligible when callback starts");
                fixture.Owner(0).ObserveFatalFailureForAdmission();
                Ensure(context[0].ReadyConnectionCount == 0, "retained provider must immediately reflect exact owner cut");
                return 0;
            }
            Ensure(calls == 2 && context.ExcludedMask == 0b011,
                "selection must monotonically exclude the cut index and finish in the same bounded loop");
            return 2;
        });
        await fixture.InitializeAsync(SharpLinkLoadBalancingStrategy.RoundRobin, admission, selector);
        Ensure(await fixture.CallAsync(800) == 2 && calls == 2, "custom cut must select the remaining eligible endpoint once");
        Ensure(admission.Acquisitions.Select(item => item.Endpoint.Id).SequenceEqual(new[] { "route-c" }) &&
               admission.Reports.Length == 1 && fixture.ReservationCount == 1,
            "now-zero bucket cannot acquire policy lease or physical reservation");
    });

    [Test]
    [Arguments(-1)]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(99)]
    public Task CustomInvalidAndThrow(int result) => RunAsync(async fixture =>
    {
        var original = new InvalidOperationException("exact custom callback sentinel");
        var selector = new SelectorProbe((in SharpLinkEndpointSelectionContext context) =>
        {
            Ensure(context.Count == 3 && context.ExcludedMask == 0b010,
                "invalid/excluded oracle requires two real eligible candidates and the held unbound middle index");
            if (result == 99) throw original;
            return result;
        });
        var admission = new AdmissionProbe();
        await fixture.InitializeAsync(SharpLinkLoadBalancingStrategy.RoundRobin, admission, selector);
        await fixture.ExpectCallFailureAsync(error => error is SharpLinkException failure &&
            failure.Code == SharpLinkErrorCode.FailedPrecondition &&
            (result == 99 ? ReferenceEquals(failure.InnerException, original) : failure.InnerException is null));
        Ensure(selector.Calls == 1 && admission.Acquisitions.Length == 0 && admission.Reports.Length == 0,
            "custom invalid index or original callback fault must not silently fall back");
    });

    [Test]
    public Task AdmissionGrantThenOwnerCut() => RunAsync(async fixture =>
    {
        var selector = FirstAvailable();
        var admission = new AdmissionProbe(candidate =>
        {
            if (candidate.Endpoint.Id == "route-a") fixture.Owner(0).ObserveFatalFailureForAdmission();
            return true;
        });
        await fixture.InitializeAsync(SharpLinkLoadBalancingStrategy.RoundRobin, admission, selector);
        var first = fixture.Owner(0);
        var second = fixture.Owner(2);
        Ensure(await fixture.CallAsync(1100) == 2, "a separate eligible endpoint receives an independent grant");
        var acquisitions = admission.Acquisitions;
        var reports = admission.Reports;
        Ensure(selector.Calls == 2 && acquisitions.Select(item => item.Endpoint.Id)
                   .SequenceEqual(new[] { "route-a", "route-c" }),
            "failed exact owner must be excluded before the second grant");
        Ensure(reports.Length == 2 && reports[0].Token == 101 && reports[1].Token == 102 &&
               reports[0].Outcome.Endpoint.Endpoint.Id == "route-a" &&
               reports[0].Outcome.Kind == SharpLinkEndpointOutcomeKind.ConnectionClosed &&
               reports[0].Outcome.ErrorCode == SharpLinkErrorCode.ConnectionClosed && !reports[0].Outcome.ResponseObserved &&
               reports[1].Outcome.Endpoint.Endpoint.Id == "route-c" &&
               reports[1].Outcome.Kind == SharpLinkEndpointOutcomeKind.Success && reports[1].Outcome.ResponseObserved,
            "each original grant reports exactly once: failed A lease, then independent successful C lease");
        Ensure(fixture.ReservationCount == 1 && ReferenceEquals(fixture.Reservations.Single(), second) &&
               first.CallAdmissionReservationCount == 0 && second.CallAdmissionReservationCount == 0,
            "only C can own a physical reservation; no A reservation or lease survives the failed exact reserve");
    });

    [Test]
    public Task AdmissionDenialNoReservation() => RunAsync(async fixture =>
    {
        var selector = FirstAvailable();
        var admission = new AdmissionProbe(candidate => candidate.Endpoint.Id != "route-a");
        await fixture.InitializeAsync(SharpLinkLoadBalancingStrategy.RoundRobin, admission, selector);
        var denied = fixture.Owner(0);
        var granted = fixture.Owner(2);
        Ensure(await fixture.CallAsync(1200) == 2, "denial must exclude A and continue with C");
        Ensure(selector.Calls == 2 && admission.Acquisitions.Select(item => item.Endpoint.Id)
                   .SequenceEqual(new[] { "route-a", "route-c" }),
            "denied endpoint cannot be selected again in the same bounded loop");
        var reports = admission.Reports;
        Ensure(reports.Length == 1 && reports[0].Token == 102 &&
               reports[0].Outcome.Endpoint.Endpoint.Id == "route-c" &&
               reports[0].Outcome.Kind == SharpLinkEndpointOutcomeKind.Success && reports[0].Outcome.ResponseObserved,
            "denied candidate receives no lease completion and no fake successful outcome");
        Ensure(fixture.ReservationCount == 1 && ReferenceEquals(fixture.Reservations.Single(), granted) &&
               denied.CallAdmissionReservationCount == 0 && denied.ActiveCallCount == 0,
            "admission denial must never enter physical reservation or pending registration");
    });

    private static SelectorProbe FirstAvailable() => new((in SharpLinkEndpointSelectionContext context) =>
    {
        Ensure(context.Count == 3 && (context.ExcludedMask & 0b010) != 0,
            "the unbound endpoint remains at its original excluded index");
        for (var index = 0; index < context.Count; index++)
            if ((context.ExcludedMask & (1UL << index)) == 0) return index;
        throw new InvalidOperationException("selector must not be invoked with no candidates");
    });

    private static bool IsNoBoundRoute(Exception error) => error is SharpLinkException
    {
        Code: SharpLinkErrorCode.Unavailable,
        DetailCode: SharpLinkErrorDetails.Unavailable.NoBoundRoute
    };

    private static async Task RunAsync(Func<ContractScopedRouteSelectionFixture, Task> body)
    {
        var fixture = new ContractScopedRouteSelectionFixture();
        Exception? primary = null;
        try { await body(fixture); }
        catch (Exception error) { primary = error; }
        await fixture.CleanupAsync(primary);
    }

    private delegate int SelectCallback(in SharpLinkEndpointSelectionContext context);
    private sealed class SelectorProbe(SelectCallback callback) : ISharpLinkEndpointSelector
    {
        internal int Calls;
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            Interlocked.Increment(ref Calls);
            return callback(in context);
        }
    }

    private sealed class AdmissionProbe(Func<SharpLinkEndpointCandidate, bool>? acquire = null) : ISharpLinkEndpointAdmissionPolicy
    {
        private readonly object _gate = new();
        private readonly List<SharpLinkEndpointCandidate> _acquisitions = [];
        private readonly List<(SharpLinkEndpointOutcome Outcome, long Token)> _reports = [];
        internal SharpLinkEndpointCandidate[] Acquisitions { get { lock (_gate) return _acquisitions.ToArray(); } }
        internal (SharpLinkEndpointOutcome Outcome, long Token)[] Reports { get { lock (_gate) return _reports.ToArray(); } }
        public SharpLinkEndpointAdmissionDecision TryAcquire(in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
        {
            long token;
            lock (_gate) { _acquisitions.Add(endpoint); token = 100 + _acquisitions.Count; }
            return new(acquire?.Invoke(endpoint) ?? true, token, null);
        }
        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            lock (_gate) _reports.Add((outcome, token));
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
