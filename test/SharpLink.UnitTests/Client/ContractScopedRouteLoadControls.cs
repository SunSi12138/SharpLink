using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

// Separate R05/R06/R09 slice. No synthetic ActiveCallCount delegate or fabricated route view.
public sealed class ContractScopedRouteLoadControls
{
    [Test]
    public Task LeastPendingLoadsAndTieCursor() => RunAsync(async fixture =>
    {
        await fixture.InitializeAsync(SharpLinkLoadBalancingStrategy.LeastPending);
        fixture.SetLoad(4, 3); // Three real active owners on A's Ready but unbound session.
        fixture.SetLoad(2, 2);
        AssertLoads(fixture, a: 3, c: 2);
        Ensure(await fixture.CallAsync(500) == 2,
            "LeastPending must count unbound Ready owner load: A total3 exceeds C total2 although A bound-only load is zero");
        fixture.SetLoad(4, 2);
        AssertLoads(fixture, a: 2, c: 2);
        var selection = fixture.View.Topology.Selection!;
        for (var call = 0; call < 6; call++)
        {
            var before = ContractScopedRouteLoadFixture.Read<int>(selection, "_leastPendingCursor");
            var start = unchecked((uint)(before + 1));
            var expected = (int)(start % 3);
            if (expected == 1) expected = 2;
            Ensure(await fixture.CallAsync(510 + call) == expected &&
                   ContractScopedRouteLoadFixture.Read<int>(selection, "_leastPendingCursor") == unchecked(before + 1),
                "equal total endpoint loads use the existing rotating full-index tie cursor once per actual selection");
            AssertLoads(fixture, a: 2, c: 2);
        }
    });

    [Test]
    public Task PowerOfTwoUsesEligibleDenominator() => RunAsync(async fixture =>
    {
        await fixture.InitializeAsync(SharpLinkLoadBalancingStrategy.PowerOfTwoChoices);
        fixture.SetLoad(0, 1);
        fixture.SetLoad(3, 1);
        fixture.SetLoad(4, 1);
        fixture.SetLoad(2, 2);
        AssertLoads(fixture, a: 3, c: 2);
        Ensure(fixture.View.Buckets[0].EligibleCount() == 2 && fixture.View.Buckets[2].EligibleCount() == 1,
            "positive capacity samples must be A2/C1, not A's three Ready physical owners");
        // Exactly two eligible endpoints makes both distinct random draw orders compare
        // this same pair. There is no probabilistic must-hit-both/distribution assertion.
        Ensure(await fixture.CallAsync(600) == 0,
            "A total active3 / eligible2 beats C total active2 / eligible1 in either draw order");
        fixture.SetLoad(4, 3);
        AssertLoads(fixture, a: 5, c: 2);
        Ensure(fixture.Owner(0).ActiveCallCount + fixture.Owner(3).ActiveCallCount == 2,
            "contrast retains A bound-only active2; only its real unbound Ready owner load changed");
        Ensure(await fixture.CallAsync(601) == 2,
            "A total active5 / eligible2 loses to C2/1; filtering numerator to bound2 or dividing by all3 Ready owners would wrongly choose A");
    });

    [Test]
    public Task CustomFullIndicesAndProviders() => RunAsync(async fixture =>
    {
        var expectedCapacity = 2;
        var expectedLoad = 3;
        var selectorCalls = 0;
        var admission = new CandidateAdmission(candidate =>
        {
            AssertCandidate(candidate, fixture, 0, expectedCapacity, expectedLoad);
        });
        var selector = new CandidateSelector((in SharpLinkEndpointSelectionContext context) =>
        {
            selectorCalls++;
            Ensure(context.Count == 3 && context.ExcludedMask == 0b010,
                "custom receives every original index, with unbound B excluded rather than removed");
            AssertCandidate(context[0], fixture, 0, expectedCapacity, expectedLoad);
            AssertCandidate(context[1], fixture, 1, 0, 0);
            AssertCandidate(context[2], fixture, 2, 1, 2);
            return 0;
        });
        await fixture.InitializeAsync(SharpLinkLoadBalancingStrategy.RoundRobin, admission, selector);
        fixture.SetLoad(0, 2);
        fixture.SetLoad(4, 1);
        fixture.SetLoad(2, 2);
        var view = fixture.View;
        var retained = view.Candidates[0];
        Ensure(await fixture.CallAsync(900) == 0, "the actual custom-selected endpoint must physically reserve and return");
        expectedLoad = 5;
        fixture.SetLoad(4, 3);
        Ensure(retained.ActiveCallCount == 5 && retained.ReadyConnectionCount == 2 && ReferenceEquals(view, fixture.View),
            "retained original provider includes changing real unbound Ready load without a rebuilt candidate/view");
        fixture.Owner(3).ObserveFatalFailureForAdmission(); // Empty bound owner cut, not the unbound load owner.
        expectedCapacity = 1;
        Ensure(retained.ReadyConnectionCount == 1 && retained.ActiveCallCount == 5 && ReferenceEquals(view, fixture.View),
            "retained capacity provider is live confirmed eligibility, not the historical Ready physical count");
        Ensure(await fixture.CallAsync(901) == 0 && selectorCalls == 2,
            "custom selection and admission must agree on the same live capacity/total-load semantics");
        var acquisitions = admission.Acquisitions;
        var reports = admission.Reports;
        Ensure(acquisitions.Length == 2 && reports.Length == 2 &&
               reports.Select(item => item.Token).SequenceEqual(new long[] { 201, 202 }) &&
               reports.All(item => item.Outcome.Kind == SharpLinkEndpointOutcomeKind.Success && item.Outcome.ResponseObserved),
            "both candidate grants complete exactly once through the actual ordinary Unary outcome path");
        Ensure(acquisitions.All(candidate => ReferenceEquals(candidate.Endpoint, retained.Endpoint) && candidate.Generation == retained.Generation),
            "admission retains the exact frozen endpoint attributes and generation, not a compacted substitute");
    });

    private static void AssertLoads(ContractScopedRouteLoadFixture fixture, int a, int c)
    {
        var view = fixture.View;
        Ensure(view.Buckets.Length == 3 && view.Buckets[0].EligibleCount() == 2 &&
               view.Buckets[1].EligibleCount() == 0 && view.Buckets[2].EligibleCount() == 1,
            "each built-in load control must enter the actual switch with two eligible endpoints plus one unbound endpoint");
        Ensure(view.Topology.Endpoints[0].Connections.Length == 3 && fixture.Owner(4).CanAcceptCalls &&
               view.Targets.All(target => !ReferenceEquals(target.Connection, fixture.Owner(4))),
            "A's third load-bearing owner is genuinely Ready and genuinely unbound for this exact contract");
        Ensure(view.Topology.Endpoints[0].ActiveCalls() == a && view.Candidates[0].ActiveCallCount == a &&
               fixture.Owner(0).ActiveCallCount + fixture.Owner(3).ActiveCallCount + fixture.Owner(4).ActiveCallCount == a &&
               view.Topology.Endpoints[2].ActiveCalls() == c && fixture.Owner(2).ActiveCallCount == c,
            "total endpoint providers must agree with original real physical owner counters");
    }

    private static void AssertCandidate(SharpLinkEndpointCandidate candidate, ContractScopedRouteLoadFixture fixture,
        int index, int capacity, int active)
    {
        var endpoint = fixture.View.Topology.Endpoints[index];
        var zone = new[] { "a", "b", "c" }[index];
        Ensure(ReferenceEquals(candidate.Endpoint, endpoint.Endpoint) && candidate.Generation == endpoint.Generation &&
               candidate.Generation == 1 && candidate.Endpoint.Id == "route-" + zone && candidate.Endpoint.Attributes["zone"] == zone &&
               candidate.ReadyConnectionCount == capacity && candidate.ActiveCallCount == active,
            "selector/admission must see original full-index endpoint identity, attributes, generation, eligible capacity and total active load");
    }

    private static async Task RunAsync(Func<ContractScopedRouteLoadFixture, Task> body)
    {
        var fixture = new ContractScopedRouteLoadFixture();
        Exception? primary = null;
        try { await body(fixture); }
        catch (Exception error) { primary = error; }
        await fixture.CleanupAsync(primary);
    }

    private delegate int SelectCallback(in SharpLinkEndpointSelectionContext context);
    private sealed class CandidateSelector(SelectCallback callback) : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context) => callback(in context);
    }

    private sealed class CandidateAdmission(Action<SharpLinkEndpointCandidate> inspect) : ISharpLinkEndpointAdmissionPolicy
    {
        private readonly object _gate = new();
        private readonly List<SharpLinkEndpointCandidate> _acquisitions = [];
        private readonly List<(SharpLinkEndpointOutcome Outcome, long Token)> _reports = [];
        internal SharpLinkEndpointCandidate[] Acquisitions { get { lock (_gate) return _acquisitions.ToArray(); } }
        internal (SharpLinkEndpointOutcome Outcome, long Token)[] Reports { get { lock (_gate) return _reports.ToArray(); } }
        public SharpLinkEndpointAdmissionDecision TryAcquire(in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
        {
            inspect(endpoint);
            lock (_gate)
            {
                _acquisitions.Add(endpoint);
                return new(true, 200 + _acquisitions.Count, null);
            }
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
