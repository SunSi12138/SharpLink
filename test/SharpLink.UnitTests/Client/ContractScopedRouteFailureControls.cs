using System.Linq;

namespace SharpLink.UnitTests.Client;

public sealed partial class ContractScopedRouteFailureControls
{
    private static readonly TimeSpan FailureBound = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task PrepareColdReconcile(bool keepConstructionFailure) => WithFixture(async fixture =>
    {
        await fixture.InitializeAsync();
        await fixture.ConfirmFirstAndPrepareAsync();
        var prior = fixture.View;
        var marker = new InvalidOperationException("R22 exact route construction marker");
        var publications = 0;
        fixture.Client.ContractRoutePublicationForTesting = stage =>
        {
            if (stage == "topology-published") Interlocked.Increment(ref publications);
        };
        fixture.Client.ContractRouteBuildForTesting = (site, stage) =>
        {
            if (ReferenceEquals(site, fixture.Site) && stage == "buckets") throw marker;
        };
        var application = fixture.StartReorder();
        await fixture.ExpectReorderFailureAsync(application, marker);
        var latest = fixture.GlobalTopology;
        Ensure(latest.Revision > prior.Topology.Revision && ReferenceEquals(fixture.View, prior),
            "failed fanout preserves the old safe site view but never rolls back published topology");
        if (keepConstructionFailure)
        {
            await fixture.ExpectPrepareFailureAsync(marker);
            Ensure(ReferenceEquals(fixture.View, prior), "failed Prepare reconciliation must not publish a partial view");
            fixture.Client.ContractRouteBuildForTesting = null;
        }
        else
        {
            fixture.Client.ContractRouteBuildForTesting = null;
            await fixture.PrepareAgainAsync();
            Ensure(ReferenceEquals(fixture.View.Topology, latest) && fixture.View.HasEligibleTarget(),
                "public Prepare itself must reconcile current T before accepting the one confirmed route");
            await fixture.CallFirstAsync(47);
        }
        Ensure(Volatile.Read(ref publications) == 1,
            "Prepare recovery/failure must not depend on a detached repair or another topology event");
    });

    [Test]
    [Arguments("targets")]
    [Arguments("buckets")]
    [Arguments("providers")]
    [Arguments("state")]
    [Arguments("signal")]
    [Arguments("state-timeout")]
    public Task SitePrebuildFailuresAreAtomic(string stage) => WithFixture(async fixture =>
    {
        await fixture.InitializeAsync(); // No confirmation: the original public Prepare is a genuine waiter.
        var prior = fixture.View;
        var first = fixture.GlobalTopology.Endpoints.Single(endpoint => endpoint.Endpoint.Id == "route-a")
            .Connections.Single();
        var before = fixture.CaptureAttempt(first);
        Ensure(!before.Completed && before.Failure is null && !before.Signal.IsCompleted && fixture.OriginalPrepareIsPending,
            "the exact existing attempt and original public Prepare waiter precede the failing Bind commit");
        var buildStage = stage == "state-timeout" ? "state" : stage;
        Exception marker = stage == "state-timeout"
            ? new TimeoutException("R23 original construction timeout, not a Prepare wait timeout")
            : new InvalidOperationException("R23 exact " + stage + " prebuild marker");
        var cleanupOutput = fixture.NewBarrier();
        var hits = 0;
        fixture.Client.ContractRouteBuildForTesting = (site, actualStage) =>
        {
            if (!ReferenceEquals(site, fixture.Site) || actualStage != buildStage || Interlocked.Increment(ref hits) != 1) return;
            fixture.HoldNextPeerOutput(0, cleanupOutput);
            throw marker;
        };
        await fixture.ConfirmAsync(0);
        await cleanupOutput.Entered.WaitAsync(FailureBound);
        var cleanup = await fixture.CaptureCandidateCleanupAsync();
        var after = fixture.CaptureAttempt(first);
        Ensure(ReferenceEquals(fixture.View, prior) && ReferenceEquals(before.Attempt, after.Attempt) &&
               !after.Completed && after.Failure is null && ReferenceEquals(after.PublicationFailure, marker) &&
               before.Signal.IsCompletedSuccessfully && ReferenceEquals(before.Signal.Result, marker),
            "failed prebuild preserves view/attempt completion but immediately publishes the exact cold failure on the existing typed signal");
        await fixture.ExpectOriginalPrepareFailureAsync(marker);
        await fixture.ExpectPrepareFailureAsync(marker);
        Ensure(!cleanup.Task.IsCompleted && fixture.OriginalBindStillPending && first.Binding.CandidateCount == 1 &&
               first.Binding.CleanupOwnerCount == 1,
            "original and fresh Prepare must fail promptly while original Bind/cleanup and their physical candidate permits remain owned");
        fixture.Client.ContractRouteBuildForTesting = null;
        cleanupOutput.Release();
        await fixture.FinishCandidateCleanupAsync(cleanup, 0);
        await fixture.ExpectOneFailedBindAsync(marker);
        Ensure(first.Binding.CandidateCount == 0 && first.Binding.CleanupOwnerCount == 0,
            "the failed exact candidate and its one cleanup owner are both physically released");
        await fixture.RetryFirstAndPrepareAsync();
        fixture.AssertCapturedNotificationStillFails(before.Signal, marker);
        await fixture.CallFirstAsync(53);
        fixture.AssertSecondStillUnconfirmed();
    });

    [Test]
    public Task PendingBuildFailureDoesNotImposeAllBoundOnFreshPrepare() => WithFixture(async fixture =>
    {
        await fixture.InitializeAsync();
        await fixture.ConfirmFirstAndPrepareAsync();
        var prior = fixture.View;
        var second = fixture.GlobalTopology.Endpoints.Single(endpoint => endpoint.Endpoint.Id == "route-b")
            .Connections.Single();
        var marker = new InvalidOperationException("R23 other-endpoint pending construction failure");
        var cleanupOutput = fixture.NewBarrier();
        fixture.Client.ContractRouteBuildForTesting = (site, stage) =>
        {
            if (!ReferenceEquals(site, fixture.Site) || stage != "state") return;
            fixture.HoldNextPeerOutput(1, cleanupOutput);
            throw marker;
        };
        await fixture.ConfirmAsync(1);
        await cleanupOutput.Entered.WaitAsync(FailureBound);
        var cleanup = await fixture.CaptureCandidateCleanupAsync();
        Ensure(ReferenceEquals(fixture.View, prior) && fixture.View.HasEligibleTarget() &&
               ReferenceEquals(fixture.CaptureAttempt(second).PublicationFailure, marker),
            "A remains exactly eligible while B owns its pending infrastructure failure");
        await fixture.PrepareAgainAsync();
        await fixture.CallFirstAsync(59, requireSecondReplyHeld: false);
        Ensure(!cleanup.Task.IsCompleted, "fresh at-least-one Prepare/RPC cannot wait for B's blocked cleanup");
        fixture.Client.ContractRouteBuildForTesting = null;
        cleanupOutput.Release();
        await fixture.FinishCandidateCleanupAsync(cleanup, 1);
        await fixture.ExpectOneFailedBindAsync(marker);
    });

    private static async Task WithFixture(Func<FailureFixture, Task> body)
    {
        var fixture = new FailureFixture();
        Exception? primary = null;
        try { await body(fixture); }
        catch (Exception error) { primary = error; }
        await fixture.CleanupAsync(primary);
    }
}
