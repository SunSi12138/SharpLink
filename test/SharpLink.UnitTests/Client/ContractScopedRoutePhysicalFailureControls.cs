using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

// v3 R25-R28. These controls require process isolation if an original owner fails to exit.
// No test hook is installed in selection, admission, invocation, or reservation release.
public sealed partial class ContractScopedRoutePhysicalFailureControls
{
    [Test]
    public Task ExternalFatalCutSurvivesTopologyPrebuildFailure() => Run(async fixture =>
    {
        await fixture.InitializeAsync();
        var oldTopology = fixture.Topology;
        var oldView = fixture.Site.CaptureContractRouteForDiagnostics();
        var marker = new InvalidOperationException("R25 topology prebuild");
        fixture.Client.ContractRoutePublicationForTesting = stage =>
        {
            if (stage == "topology-prebuild") throw marker;
        };
        fixture.FaultActualSession();
        await fixture.Peer.DisposalEntered.Task.WaitAsync(Bound);
        var cleanup = await fixture.CaptureCleanupAsync("StaticClusterDisconnectedConnectionCleanup");
        Ensure(ReferenceEquals(fixture.Topology, oldTopology) &&
               ReferenceEquals(fixture.Site.CaptureContractRouteForDiagnostics(), oldView),
            "failed prebuild preserves both exact immutable authorities");
        Ensure(!oldView.Targets.Single(target => ReferenceEquals(target.Connection, fixture.Connection)).IsEligible &&
               !fixture.Connection.TryReserveExactCallAdmission(),
            "the externally fatal physical cut makes even an old confirmed target ineligible");
        Ensure(!fixture.ContainsPhysicalOwner() && !cleanup.Source.IsCompleted,
            "dead physical owner is detached and its same required cleanup is pending real Dispose");
        fixture.ClearHooks();
        fixture.Peer.ReleaseDisposal();
        await fixture.AcceptCleanupAsync(cleanup, marker, disposalGraph: null, topologyCommitted: false);
        Ensure(fixture.Peer.DisposalCount == 1 && fixture.Peer.InnerDispose is { IsCompletedSuccessfully: true },
            "real transport disposal exits once despite failed next-topology construction");
    });

    [Test]
    public Task DisconnectCallbackRetainsFanoutAndIndependentDisposeGraph() => Run(async fixture =>
    {
        await fixture.InitializeAsync();
        var marker = new InvalidOperationException("R26 site fanout");
        var dispose = new InvalidOperationException("R26 independent Dispose occurrence");
        var sibling = new IOException("R26 independent Dispose sibling");
        var graph = new AggregateException(dispose, dispose, sibling);
        fixture.Peer.DisposeFailure = graph;
        fixture.FailSiteBuild(marker);
        fixture.FaultActualSession(); // Actual private RpcSession.Fault; invokes its swallowing callback path.
        await fixture.Peer.DisposalEntered.Task.WaitAsync(Bound);
        var cleanup = await fixture.CaptureCleanupAsync("StaticClusterDisconnectedConnectionCleanup");
        Ensure(!cleanup.Source.IsCompleted && !fixture.ContainsPhysicalOwner(),
            "the callback-returning event source cannot substitute for required physical cleanup completion");
        fixture.ClearHooks();
        fixture.Peer.ReleaseDisposal();
        await fixture.AcceptCleanupAsync(cleanup, marker, graph, topologyCommitted: true);
        Ensure(fixture.Peer.DisposalCount == 1 && fixture.Peer.InnerDispose is { IsCompletedSuccessfully: true },
            "the independent throwing Dispose still joins actual peer teardown exactly once");
    });

    [Test]
    public Task RouteCancellationAndDisposeFaultSurviveSealedShutdown() => Run(async fixture =>
    {
        await fixture.InitializeAsync();
        var marker = new OperationCanceledException("R27 route construction is NOT task cancellation");
        var dispose = new IOException("R27 independent Dispose occurrence");
        var graph = new AggregateException(dispose, dispose);
        fixture.Peer.DisposeFailure = graph;
        fixture.FailSiteBuild(marker);
        fixture.FaultActualSession();
        await fixture.Peer.DisposalEntered.Task.WaitAsync(Bound);
        var cleanup = await fixture.CaptureCleanupAsync("StaticClusterDisconnectedConnectionCleanup");
        fixture.ClearHooks();
        var stop = fixture.StartStop();
        await fixture.WaitForSupervisorSealedAsync();
        Ensure(!stop.Source.IsCompleted && !cleanup.Source.IsCompleted,
            "shutdown is sealed while the original physical cleanup remains genuinely held");
        fixture.Peer.ReleaseDisposal();
        await fixture.AcceptCleanupAsync(cleanup, marker, graph, topologyCommitted: true);
        await fixture.AcceptLifecycleAsync(stop);
        Ensure(cleanup.Source.IsFaulted && stop.Source.IsFaulted,
            "route OCE survives as a faulted envelope, not canceled or expected-shutdown suppression");
    });

    [Test]
    public Task DeferredDrainFailureMovesToOriginalDetachCleanup() => Run(async fixture =>
    {
        await fixture.InitializeAsync();
        var marker = new InvalidOperationException("R28 deferred first safety cut");
        fixture.Reserve();
        var fanouts = 0;
        fixture.Client.ContractRouteBuildForTesting = (site, stage) =>
        {
            if (ReferenceEquals(site, fixture.Site) && stage == "buckets")
            {
                Interlocked.Increment(ref fanouts);
                throw marker;
            }
        };
        fixture.Connection.OnBindingSessionDraining();
        var slots = fixture.CaptureDeferredSlots();
        Ensure(slots.Cut is not null && slots.Detach is null &&
               fixture.Connection.CallAdmissionReservationCount == 1 && fixture.ContainsPhysicalOwner() &&
               !fixture.Peer.DisposalEntered.Task.IsCompleted,
            "one active original reservation retains the draining owner and its first bounded failure slot");
        AssertOccurrences(slots.Cut!, marker);
        Ensure(slots.Cut!.TopologyCommitted && slots.Cut.Count == 1 && slots.Cut.StageAt(0) == "site-reconcile",
            "the first deferred slot is exactly the committed one-site fanout batch");
        var afterCut = fixture.Topology;
        for (var index = 0; index < 8; index++) fixture.DuplicateClusterDrain();
        var afterDuplicates = fixture.CaptureDeferredSlots();
        Ensure(ReferenceEquals(afterDuplicates.Cut, slots.Cut) && afterDuplicates.Detach is null &&
               ReferenceEquals(fixture.Topology, afterCut) && Volatile.Read(ref fanouts) == 1,
            "duplicate drain neither republishes/fans out nor appends another error slot");
        fixture.ClearHooks();
        fixture.ReleaseReservation(); // Original physical release triggers the existing idle detach path.
        await fixture.Peer.DisposalEntered.Task.WaitAsync(Bound);
        var cleanup = await fixture.CaptureCleanupAsync("StaticClusterIdleConnectionCleanup");
        var transferred = fixture.CaptureDeferredSlots();
        Ensure(transferred.Cut is null && transferred.Detach is null && !fixture.ContainsPhysicalOwner() &&
               fixture.Connection.CallAdmissionReservationCount == 0 && !cleanup.Source.IsCompleted,
            "detach moves the two bounded slots into the same required cleanup task, without a reporting proxy");
        fixture.Peer.ReleaseDisposal();
        await fixture.AcceptCleanupAsync(cleanup, marker, disposalGraph: null, topologyCommitted: true);
    });

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static async Task Run(Func<PhysicalFixture, Task> body, bool holdSecond = false)
    {
        var fixture = new PhysicalFixture(holdSecond);
        Exception? primary = null;
        try { await body(fixture); }
        catch (Exception error) { primary = error; }
        await fixture.CleanupAsync(primary);
    }

    // Traversal is occurrence-based. Never HashSet/deduplicate Exception references: the same
    // object twice within a Dispose graph remains two independent stage occurrences.
    private static void AssertOccurrences(Exception root, params Exception[] expected)
    {
        var actual = Leaves(root).ToArray();
        Ensure(actual.Length == expected.Length, $"Unexpected complete failure graph: {root}");
        for (var index = 0; index < expected.Length; index++)
            Ensure(ReferenceEquals(actual[index], expected[index]), $"Failure occurrence {index} lost identity/order: {root}");
    }

    private static IEnumerable<Exception> Leaves(Exception error)
    {
        if (error is SharpLinkClient.ContractRouteFailure route)
        {
            Ensure(route.Count != 0, "an unknown empty route envelope is not an expected failure");
            for (var index = 0; index < route.Count; index++)
            {
                Ensure(!string.IsNullOrWhiteSpace(route.StageAt(index)), "each route occurrence retains its owner stage");
                foreach (var leaf in Leaves(route.ErrorAt(index))) yield return leaf;
            }
        }
        else if (error is AggregateException aggregate)
        {
            Ensure(aggregate.InnerExceptions.Count != 0, "an unknown empty AggregateException is not whitelisted");
            foreach (var nested in aggregate.InnerExceptions)
                foreach (var leaf in Leaves(nested)) yield return leaf;
        }
        else yield return error;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
